using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ChefGptCrawler
{
    class QAGenerator
    {
        static readonly string SystemPrompt =
            "Sen ChefGPT, Türk mutfağında uzmanlaşmış bir yapay zeka aşçısısın. " +
            "Kullanıcılara yemek tarifleri öneriyorsun, malzemelere göre tarif buluyorsun " +
            "ve yemeklerle ilgili soruları yanıtlıyorsun. Cevaplarını samimi, sıcak ve " +
            "yardımsever bir dille ver.";

        static readonly Random rng = new Random(42);
        static readonly string QaFilePath = "chefgpt_qa_dataset.jsonl";

        static readonly JsonSerializerOptions jsonOpts = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        // ============================================================
        // SCRAPER ENTEGRASYONU — Her tarif çekildiğinde çağrılır
        // ============================================================

        public static void AppendQAForRecipe(Dictionary<string, string> row)
        {
            var conversations = ProcessRow(row);
            if (conversations.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var conv in conversations)
            {
                sb.AppendLine(JsonSerializer.Serialize(conv, jsonOpts));
            }
            File.AppendAllText(QaFilePath, sb.ToString(), Encoding.UTF8);
        }

        // ============================================================
        // TOPLU ÜRETİM — CSV'den oku, JSONL üret
        // ============================================================

        public static void Generate(string csvPath, string outputPath)
        {
            if (!File.Exists(csvPath))
            {
                Console.WriteLine($"HATA: CSV dosyası bulunamadı: {csvPath}");
                return;
            }

            Console.WriteLine($"📖 CSV okunuyor: {csvPath}");
            var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
            if (lines.Length < 2) { Console.WriteLine("CSV boş."); return; }

            var headers = ParseCsvLine(lines[0]);
            int totalRecipes = 0, totalQA = 0, skipped = 0;
            var kategoriStats = new Dictionary<string, int>();

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var values = ParseCsvLine(lines[i]);
                if (values.Length < headers.Length) continue;

                var row = new Dictionary<string, string>();
                for (int j = 0; j < headers.Length && j < values.Length; j++)
                    row[headers[j]] = values[j];

                totalRecipes++;
                string baslik = Get(row, "Baslik");
                if (string.IsNullOrEmpty(baslik) || baslik == "Yok") { skipped++; continue; }

                var conversations = ProcessRow(row);
                foreach (var conv in conversations)
                {
                    writer.WriteLine(JsonSerializer.Serialize(conv, jsonOpts));
                    totalQA++;
                }

                string kat = Get(row, "Kategori");
                string anaKat = kat.Contains(" > ") ? kat.Split(" > ")[0] : kat;
                kategoriStats[anaKat] = kategoriStats.GetValueOrDefault(anaKat, 0) + 1;

                if (totalRecipes % 500 == 0)
                    Console.WriteLine($"  ... {totalRecipes} tarif → {totalQA} Q&A");
            }

            Console.WriteLine($"\n{"".PadLeft(50, '=')}");
            Console.WriteLine($"✅ TAMAMLANDI!");
            Console.WriteLine($"📊 Toplam tarif: {totalRecipes}");
            Console.WriteLine($"❌ Atlanan: {skipped}");
            Console.WriteLine($"💬 Üretilen Q&A: {totalQA}");
            Console.WriteLine($"📁 Çıktı: {outputPath}");
            Console.WriteLine($"📏 Boyut: {new FileInfo(outputPath).Length / (1024.0 * 1024.0):F2} MB");

            Console.WriteLine($"\n📂 Kategori dağılımı:");
            foreach (var kv in kategoriStats.OrderByDescending(x => x.Value).Take(15))
                Console.WriteLine($"  {kv.Key,-35} {kv.Value,5} {new string('█', Math.Min(kv.Value / 5, 40))}");

            int valid = totalRecipes - skipped;
            Console.WriteLine($"\n📐 Tarif başına ort. Q&A: {(valid > 0 ? (double)totalQA / valid : 0):F1}");
        }

        // ============================================================
        // Q&A ÜRETİM — Her tarif için 6+ tip soru
        // ============================================================

        static List<object> ProcessRow(Dictionary<string, string> row)
        {
            var convs = new List<object>();
            string baslik = Get(row, "Baslik");
            if (string.IsNullOrEmpty(baslik) || baslik == "Yok") return convs;

            string baslikLower = baslik.ToLowerInvariant();
            string malzemeler = Get(row, "Malzemeler");
            string kalori = Get(row, "Kalori");
            string besin = Get(row, "Besin_Degerleri");
            string kategori = Get(row, "Kategori");
            string hz = Get(row, "Hazirlik_Suresi");
            string ps = Get(row, "Pisirme_Suresi");
            string kisi = Get(row, "Kisi_Sayisi");

            // ─── TİP 1: TARİF SORUSU (her zaman, 2 farklı şablon) ───
            convs.Add(MakeConv(PickTarifSoru(baslik, baslikLower), MakeTarifCevap(row)));
            convs.Add(MakeConv(PickTarifSoru(baslik, baslikLower), MakeTarifCevap(row)));

            // ─── TİP 2: MALZEME İLE TARİF ÖNERİSİ ───
            if (malzemeler != "Yok")
            {
                string q = MakeMalzemeSoru(malzemeler);
                if (q != null) convs.Add(MakeConv(q, MakeMalzemeCevap(row)));
            }

            // ─── TİP 3: KALORİ / BESİN DEĞERİ ───
            if (kalori != "Yok" || besin != "Yok")
                convs.Add(MakeConv(PickKaloriSoru(baslik, baslikLower), MakeKaloriCevap(row)));

            // ─── TİP 4: KATEGORİ ÖNERİSİ ───
            if (kategori != "Genel" && !string.IsNullOrWhiteSpace(kategori))
                convs.Add(MakeConv(PickKategoriSoru(kategori), MakeKategoriCevap(row)));

            // ─── TİP 5: SÜRE SORUSU ───
            if (hz != "Yok" || ps != "Yok")
                convs.Add(MakeConv(PickSureSoru(baslik, baslikLower), MakeSureCevap(row)));

            // ─── TİP 6: MALZEME LİSTESİ ───
            if (malzemeler != "Yok")
                convs.Add(MakeConv(PickMalzemeListeSoru(baslik, baslikLower), MakeMalzemeListeCevap(row)));

            // ─── TİP 7: KİŞİ SAYISI / PORSİYON ───
            if (kisi != "Yok")
                convs.Add(MakeConv(PickKisiSoru(baslik, baslikLower), MakeKisiCevap(row)));

            // ─── TİP 8: PRATİKLİK / ZORLUK ───
            convs.Add(MakeConv(PickPratikSoru(baslik, baslikLower), MakePratikCevap(row)));

            return convs;
        }

        // ============================================================
        //  SORU ŞABLONLARI — Her tip 15-20 doğal varyasyon
        // ============================================================

        static string PickTarifSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} nasıl yapılır?",
                $"{b} tarifi verir misin?",
                $"Bana {bl} tarifi anlatır mısın?",
                $"{b} yapmak istiyorum, nasıl yapacağım?",
                $"{b} için tarif lazım.",
                $"{b} tarifini adım adım anlatır mısın?",
                $"Evde {bl} yapmak istiyorum, tarifi nedir?",
                $"{b} yapımı hakkında bilgi verir misin?",
                $"Bugün {bl} deneyeceğim, tarifi nasıl?",
                $"{bl} ilk defa yapacağım, yardımcı olur musun?",
                $"Anneannem {bl} yapardı ama tarifini bilmiyorum, anlatır mısın?",
                $"Misafirlere {bl} yapmayı düşünüyorum, tarifi nedir?",
                $"{b} yapma rehberi verebilir misin?",
                $"{bl} en kolay nasıl yapılır?",
                $"Pratik {bl} tarifi var mı?",
                $"{b} için detaylı bir tarif verir misin?",
                $"Arkadaşlarıma {bl} yapmak istiyorum nasıl yaparım?",
                $"{bl} hazırlamak istiyorum ne yapmalıyım?",
                $"Şefim {bl} tarifini paylaşır mısın?",
                $"{b} pişirmek istiyorum yardım eder misin?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickKaloriSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} kaç kalori?",
                $"{b} kalori değeri nedir?",
                $"{bl} diyete uygun mu?",
                $"{b} besin değerleri nelerdir?",
                $"Bir porsiyon {bl} kaç kalori yapar?",
                $"{bl} yersem kaç kalori almış olurum?",
                $"Diyet yapıyorum {bl} yiyebilir miyim?",
                $"{b} sağlıklı mı? Kalori değeri ne kadar?",
                $"{bl} kilo aldırır mı?",
                $"{b} besleyici mi? İçinde ne var?",
                $"Spor sonrası {bl} yenir mi? Besin değerleri nasıl?",
                $"Kalori hesabı yapıyorum {bl} kaç kalori acaba?",
                $"{b} protein değeri ne kadar?",
                $"{bl} makro değerleri nedir?",
                $"Sağlık açısından {bl} nasıl değerlendirirsin?",
                $"Fit yaşam için {bl} uygun mu?",
                $"{b} kaç kalorili bir yemek?",
                $"Rejim yapıyorum {bl} listeme ekleyebilir miyim?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickKategoriSoru(string kategori)
        {
            string anaKat = kategori.Contains(" > ") ? kategori.Split(" > ")[0] : kategori;
            string anaKatLower = anaKat.ToLowerInvariant();
            var t = new[]
            {
                $"Bana bir {anaKatLower} tarifi önerir misin?",
                $"{anaKat} kategorisinden ne yapabilirim?",
                $"Bugün {anaKatLower} yapmak istiyorum ne önerirsin?",
                $"Güzel bir {anaKatLower} tarifi arıyorum.",
                $"{anaKatLower} yemek istiyorum ne yapayım?",
                $"Akşama {anaKatLower} düşünüyorum önerilerin var mı?",
                $"Kolay bir {anaKatLower} tarifi söyler misin?",
                $"{anaKat} çeşitlerinden bir tarif paylaşır mısın?",
                $"Farklı bir {anaKatLower} denemek istiyorum ne yapabilirim?",
                $"Ailem {anaKatLower} çok seviyor güzel bir tarif var mı?",
                $"Hafta sonu için {anaKatLower} önerisi alabilir miyim?",
                $"Çocuklara {anaKatLower} yapmak istiyorum ne yapayım?",
                $"En sevilen {anaKatLower} tarifleri neler?",
                $"Misafir gelecek {anaKatLower} yapayım mı sence?",
                $"Yeni {anaKatLower} tarifleri denemek istiyorum önerin var mı?",
                $"{anaKat} yapacağım fikir verir misin?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickSureSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} ne kadar sürede hazırlanır?",
                $"{b} yapmak ne kadar sürer?",
                $"{b} için ne kadar zaman ayırmalıyım?",
                $"{bl} kaç dakikada hazır olur?",
                $"{bl} hazırlamak uzun sürer mi?",
                $"Acelem var {bl} hızlı yapılır mı?",
                $"{b} toplam ne kadar sürede pişer?",
                $"{bl} hazırlık ve pişirme süresi ne kadar?",
                $"Yarım saatim var {bl} yetişir mi?",
                $"{bl} için mutfakta ne kadar vakit geçirmem lazım?",
                $"İş çıkışı {bl} yapacak vaktim var mı?",
                $"{b} pişirme süresi kaç dakika?",
                $"Hızlıca {bl} yapılabilir mi?",
                $"{bl} zamanımı çok alır mı?",
                $"{b} hazırlaması kolay mı uzun mu sürer?",
                $"15 dakikam var {bl} yetiştirebilir miyim?",
                $"{bl} sabah hazırlasam akşama yetişir mi?",
                $"Öğle arasında {bl} yapabilir miyim süre olarak?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickMalzemeListeSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} için ne malzeme lazım?",
                $"{b} malzemeleri neler?",
                $"{b} yapmak için neler almalıyım?",
                $"{bl} için marketten ne almam gerekiyor?",
                $"{bl} malzeme listesini verir misin?",
                $"{b} yapmak için gereken şeyler neler?",
                $"{bl} için alışveriş listesi hazırlar mısın?",
                $"Evde {bl} yapacağım neye ihtiyacım var?",
                $"{b} hangi malzemelerle yapılır?",
                $"{bl} için gerekli malzemeleri söyler misin?",
                $"Mutfağımda {bl} için ne bulunmalı?",
                $"{b} yapmadan önce ne hazırlamalıyım?",
                $"{bl} kaç malzeme ile yapılır?",
                $"{b} için gereken ürünler neler?",
                $"Markete gidiyorum {bl} için ne alayım?",
                $"{bl} hangi ürünlerle hazırlanır?",
                $"{b} yapımı için neler gerekli?",
                $"Dolabımda ne olmalı ki {bl} yapabileyim?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickKisiSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} kaç kişilik?",
                $"{b} kaç kişiye yeter?",
                $"Bu {bl} tarifi kaç porsiyon çıkarır?",
                $"{bl} tarifi kaç kişilik oluyor?",
                $"4 kişilik {bl} yapabilir miyim?",
                $"{bl} ne kadar kişiye yetecek miktarda?",
                $"Misafir gelecek {bl} herkese yeter mi?",
                $"Tek kişilik {bl} yapabilir miyim?",
                $"{b} kaç kişiye yetecek şekilde hazırlanır?",
                $"Aile için {bl} yeterli mi porsiyon olarak?",
                $"{bl} kaç porsiyon oluyor?",
                $"Büyük bir aileye {bl} yetsn diye ne kadar yapmalıyım?",
                $"{b} ölçüleri kaç kişilik?",
                $"İki kişiye {bl} yeter mi bu tarifle?",
                $"{bl} porsiyonu nasıl ayarlarım?",
            };
            return t[rng.Next(t.Length)];
        }

        static string PickPratikSoru(string b, string bl)
        {
            var t = new[]
            {
                $"{b} zor mu yapması?",
                $"{bl} yapmak kolay mı?",
                $"Yeni başlayanlar {bl} yapabilir mi?",
                $"{bl} için mutfak tecrübesi gerekiyor mu?",
                $"{b} pratik bir tarif mi?",
                $"Hiç yemek yapmadım {bl} deneyebilir miyim?",
                $"{bl} acemiler için uygun mu?",
                $"Çok uğraştırır mı {bl}?",
                $"{b} yapması zahmetli mi?",
                $"Mutfakta yeniyim {bl} başlangıç için uygun mu?",
                $"{bl} zor bir tarif mi kolay mı?",
                $"İlk defa deneyecek birine {bl} önerir misin?",
                $"{b} yapımı karmaşık mı?",
                $"Çocuğumla birlikte {bl} yapabilir miyiz?",
                $"{bl} tek başıma halledebilir miyim?",
            };
            return t[rng.Next(t.Length)];
        }

        static string? MakeMalzemeSoru(string malzemelerStr)
        {
            var malzList = malzemelerStr.Split(" | ").Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            if (malzList.Count < 2) return null;

            int count = Math.Min(rng.Next(2, 5), malzList.Count);
            var secilen = malzList.OrderBy(_ => rng.Next()).Take(count).ToList();

            var sadeMalz = new List<string>();
            foreach (var m in secilen)
            {
                var words = m.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 3)
                    sadeMalz.Add(string.Join(' ', words.Skip(words.Length - 2)));
                else if (words.Length >= 1)
                    sadeMalz.Add(words.Last());
            }
            string malzText = string.Join(", ", sadeMalz);

            var t = new[]
            {
                $"Elimde {malzText} var. Ne yapabilirim?",
                $"{malzText} ile ne yemek yapılır?",
                $"Evde {malzText} var, bir tarif önerir misin?",
                $"{malzText} kullanarak ne pişirebilirim?",
                $"Buzdolabımda {malzText} kalmış, bunlarla ne yaparım?",
                $"Sadece {malzText} var elimde bir şeyler yapabilir miyim?",
                $"{malzText} var, akşama ne pişirsem?",
                $"Dolaptaki {malzText} ile güzel bir yemek çıkar mı?",
                $"Market kapalı {malzText} ile idare edebilir miyim?",
                $"{malzText} değerlendirmek istiyorum tarif öner",
                $"Elime geçen {malzText} ile ne yapılabilir?",
                $"Bugün {malzText} kullanmak istiyorum ne pişireyim?",
                $"{malzText} birleşince ne olur, tarif var mı?",
                $"Hızlı bir şeyler yapmam lazım {malzText} var ne yapayım?",
                $"Çocuklara {malzText} ile bir şeyler hazırlayabilir miyim?",
            };
            return t[rng.Next(t.Length)];
        }

        // ============================================================
        //  CEVAP ÜRETİCİLER
        // ============================================================

        static string MakeTarifCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🍳 **{row["Baslik"]}**\n");
            AppendIf(sb, row, "Kisi_Sayisi", "📌 ");
            AppendIf(sb, row, "Hazirlik_Suresi", "⏱️ Hazırlık: ");
            AppendIf(sb, row, "Pisirme_Suresi", "🔥 Pişirme: ");
            AppendMalz(sb, row);
            AppendYap(sb, row);
            AppendIf(sb, row, "Kalori", "\n🔢 Kalori: ");
            sb.AppendLine("\nAfiyet olsun! 😊");
            return sb.ToString().Trim();
        }

        static string MakeMalzemeCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Bu malzemelerle **{row["Baslik"]}** yapabilirsin! 🎉\n");
            AppendIf(sb, row, "Kisi_Sayisi", "📌 ");
            string hz = Get(row, "Hazirlik_Suresi"), ps = Get(row, "Pisirme_Suresi");
            if (hz != "Yok") sb.AppendLine($"⏱️ Süre: Hazırlık {hz}" + (ps != "Yok" ? $" + Pişirme {ps}" : ""));
            AppendMalz(sb, row);
            AppendYap(sb, row);
            sb.AppendLine("\nAfiyet olsun! 😊");
            return sb.ToString().Trim();
        }

        static string MakeKaloriCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{row["Baslik"]}** besin değerleri:\n");
            string k = Get(row, "Kalori");
            sb.AppendLine(k != "Yok" ? $"🔢 Kalori: {k}" : "🔢 Kalori bilgisi mevcut değil.");
            string b = Get(row, "Besin_Degerleri");
            if (b != "Yok")
            {
                sb.AppendLine("\n📊 **Detaylı besin değerleri:**");
                foreach (var item in b.Split(" | ")) sb.AppendLine($"• {item.Trim()}");
            }
            AppendIf(sb, row, "Kisi_Sayisi", "\n📌 Bu değerler ", " için geçerlidir.");
            return sb.ToString().Trim();
        }

        static string MakeKategoriCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Sana **{row["Baslik"]}** tarifini önerebilirim! 🌟\n");
            sb.AppendLine($"📂 Kategori: {Get(row, "Kategori")}");
            AppendIf(sb, row, "Kisi_Sayisi", "📌 ");
            AppendIf(sb, row, "Hazirlik_Suresi", "⏱️ Hazırlık: ");
            AppendMalz(sb, row, 8);
            AppendYap(sb, row);
            sb.AppendLine("\nAfiyet olsun! 😊");
            return sb.ToString().Trim();
        }

        static string MakeSureCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{row["Baslik"]}** için gereken süreler:\n");
            string hz = Get(row, "Hazirlik_Suresi"), ps = Get(row, "Pisirme_Suresi");
            if (hz != "Yok") sb.AppendLine($"⏱️ Hazırlık süresi: {hz}");
            if (ps != "Yok") sb.AppendLine($"🔥 Pişirme süresi: {ps}");
            if (hz != "Yok" && ps != "Yok")
                sb.AppendLine($"\n📌 Toplam yaklaşık {hz} hazırlık + {ps} pişirme süresi ayırmalısın.");
            AppendIf(sb, row, "Kisi_Sayisi", "👥 Bu tarif ", " içindir.");
            return sb.ToString().Trim();
        }

        static string MakeMalzemeListeCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{row["Baslik"]}** için gereken malzemeler:\n");
            AppendIf(sb, row, "Kisi_Sayisi", "📌 ");
            string m = Get(row, "Malzemeler");
            if (m != "Yok")
            {
                var list = m.Split(" | ");
                for (int i = 0; i < list.Length; i++) sb.AppendLine($"{i + 1}. {list[i].Trim()}");
                sb.AppendLine($"\n🛒 Toplam {list.Length} malzeme gerekiyor.");
            }
            return sb.ToString().Trim();
        }

        static string MakeKisiCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            string kisi = Get(row, "Kisi_Sayisi");
            sb.AppendLine($"**{row["Baslik"]}** tarifi **{kisi}** olarak hazırlanır.\n");
            string hz = Get(row, "Hazirlik_Suresi"), ps = Get(row, "Pisirme_Suresi");
            if (hz != "Yok") sb.AppendLine($"⏱️ Hazırlık: {hz}");
            if (ps != "Yok") sb.AppendLine($"🔥 Pişirme: {ps}");
            string m = Get(row, "Malzemeler");
            if (m != "Yok")
                sb.AppendLine($"\n📋 Toplam {m.Split(" | ").Length} malzeme ile hazırlanır.");
            sb.AppendLine("\n💡 Kişi sayısını artırmak için malzeme miktarlarını oranla çarpabilirsiniz.");
            return sb.ToString().Trim();
        }

        static string MakePratikCevap(Dictionary<string, string> row)
        {
            var sb = new StringBuilder();
            string hz = Get(row, "Hazirlik_Suresi"), ps = Get(row, "Pisirme_Suresi");
            string m = Get(row, "Malzemeler");
            int malzCount = m != "Yok" ? m.Split(" | ").Length : 0;

            sb.AppendLine($"**{row["Baslik"]}** hakkında pratiklik değerlendirmesi:\n");

            // Basit zorluk tahmini
            bool hizli = false;
            if (hz != "Yok" && (hz.Contains("5dk") || hz.Contains("10dk") || hz.Contains("5 dk") || hz.Contains("10 dk")))
                hizli = true;

            if (hizli && malzCount <= 8)
                sb.AppendLine("✅ Bu tarif oldukça kolay ve pratik! Yeni başlayanlar rahatlıkla yapabilir.");
            else if (malzCount <= 12)
                sb.AppendLine("✅ Orta zorlukta bir tarif. Biraz mutfak deneyimi yeterli.");
            else
                sb.AppendLine("⚠️ Bu tarif biraz fazla malzeme ve emek gerektiriyor ama sonucu harika!");

            if (hz != "Yok") sb.AppendLine($"⏱️ Hazırlık: {hz}");
            if (ps != "Yok") sb.AppendLine($"🔥 Pişirme: {ps}");
            if (malzCount > 0) sb.AppendLine($"📋 Malzeme sayısı: {malzCount}");

            sb.AppendLine("\n💡 Tarifte adım adım ilerlerseniz çok kolay halledersiniz!");
            return sb.ToString().Trim();
        }

        // ============================================================
        //  YARDIMCI METOTLAR
        // ============================================================

        static object MakeConv(string user, string assistant) => new
        {
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = user },
                new { role = "assistant", content = assistant }
            }
        };

        static string Get(Dictionary<string, string> row, string key) =>
            row.GetValueOrDefault(key, "Yok")?.Trim() ?? "Yok";

        static void AppendIf(StringBuilder sb, Dictionary<string, string> row, string key, string pre, string suf = "")
        {
            string v = Get(row, key);
            if (v != "Yok" && !string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{pre}{v}{suf}");
        }

        static void AppendMalz(StringBuilder sb, Dictionary<string, string> row, int max = 0)
        {
            string m = Get(row, "Malzemeler");
            if (m == "Yok") return;
            var list = m.Split(" | ");
            sb.AppendLine($"\n📋 **Malzemeler:**");
            int lim = max > 0 ? Math.Min(max, list.Length) : list.Length;
            for (int i = 0; i < lim; i++) sb.AppendLine($"• {list[i].Trim()}");
            if (max > 0 && list.Length > max) sb.AppendLine($"  ...ve {list.Length - max} malzeme daha");
        }

        static void AppendYap(StringBuilder sb, Dictionary<string, string> row)
        {
            string y = Get(row, "Yapilisi");
            if (y != "Yok") sb.AppendLine($"\n👨‍🍳 **Yapılışı:**\n{y}");
        }

        // ============================================================
        //  CSV PARSER
        // ============================================================

        public static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQ = false;
            var cur = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else inQ = false; }
                    else cur.Append(c);
                }
                else
                {
                    if (c == '"') inQ = true;
                    else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                    else cur.Append(c);
                }
            }
            fields.Add(cur.ToString());
            return fields.ToArray();
        }
    }
}
