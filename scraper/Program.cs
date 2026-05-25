using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace ChefGptCrawler
{
    class Program
    {
        static readonly string CsvFilePath = "nefis_yemek_tarifleri_dataset.csv";
        static readonly string LastPageFilePath = "son_sayfa.txt";
        static readonly string BaseUrl = "https://www.nefisyemektarifleri.com";
        static readonly Random random = new Random();

        static async Task Main(string[] args)
        {
            // Q&A dataset üretimi modu
            if (args.Length > 0 && args[0] == "--generate-qa")
            {
                string csvInput = args.Length > 1 ? args[1] : CsvFilePath;
                string qaOutput = args.Length > 2 ? args[2] : "chefgpt_qa_dataset.jsonl";
                QAGenerator.Generate(csvInput, qaOutput);
                return;
            }

            Console.WriteLine("Chef GPT Crawler Başlatılıyor (Kesintisiz İnatçı Mod), kirvem...\n");

            HashSet<string> scrapedUrls = new HashSet<string>();

            // CSV kontrolü ve daha önce çekilenleri listeye alma
            if (File.Exists(CsvFilePath))
            {
                var lines = File.ReadAllLines(CsvFilePath);
                for (int i = 1; i < lines.Length; i++)
                {
                    var columns = lines[i].Split(new[] { "\",\"" }, StringSplitOptions.None);
                    if (columns.Length > 0)
                        scrapedUrls.Add(columns[0].Trim('"'));
                }
                Console.WriteLine($"Önceden çekilmiş {scrapedUrls.Count} tarif var. Tekrarlananlar atlanacak...");
            }
            else
            {
                string header = "\"URL\",\"Kategori\",\"Baslik\",\"Kisi_Sayisi\",\"Hazirlik_Suresi\",\"Pisirme_Suresi\",\"Malzemeler\",\"Yapilisi\",\"Kalori\",\"Besin_Degerleri\",\"Yapay_Zeka_Ozeti\",\"Ekstra_Bilgiler\"\n";
                File.WriteAllText(CsvFilePath, header, Encoding.UTF8);
            }

            // Kaldığımız sayfayı bulma
            int startPage = 2;
            if (File.Exists(LastPageFilePath))
            {
                string savedPageStr = File.ReadAllText(LastPageFilePath).Trim();
                if (int.TryParse(savedPageStr, out int savedPage))
                {
                    startPage = savedPage;
                    Console.WriteLine($"Kayıt bulundu! {startPage}. sayfadan devam ediliyor...\n");
                }
            }

            var handler = new HttpClientHandler { UseCookies = true };
            using HttpClient client = new HttpClient(handler);

            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");

            // 40781. sayfaya kadar döngü
            for (int page = startPage; page <= 40781; page++)
            {
                string listPageUrl = $"{BaseUrl}/kategori/tarifler/page/{page}/";
                Console.WriteLine($"\n>>> Sayfa {page} taranıyor...");

                bool isPageSuccessful = false;
                int retryCount = 0;

                while (!isPageSuccessful && retryCount < 3)
                {
                    try
                    {
                        var response = await SendRequestWithRandomIpAsync(client, listPageUrl);

                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Console.WriteLine($"  -> Sayfa {page} 404 verdi. Atlanıp sıradaki sayfaya geçiliyor...");
                            isPageSuccessful = true; // Tekrar denemesin diye true yapıyoruz
                            break; // İç döngüyü kırar, dışarıdaki for döngüsü page'i artırıp devam eder
                        }
                        else if (response.StatusCode == (HttpStatusCode)429)
                        {
                            Console.WriteLine("    [UYARI] 429 Ban Yedik! Sunucu 30 saniye dinlendiriliyor...");
                            await Task.Delay(30000);
                            retryCount++;
                            continue;
                        }

                        var html = await response.Content.ReadAsStringAsync();
                        var doc = new HtmlDocument();
                        doc.LoadHtml(html);

                        // Birden fazla XPath dene — class'ta ek CSS class varsa ilk selector kaçırabilir
                        var recipeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'recipe-cards')]//figure[contains(@class, 'recipe-image')]/a[@href]");

                        // Fallback: data-jshref kullanan kartlar
                        if (recipeNodes == null || recipeNodes.Count == 0)
                            recipeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'recipe-cards')]//a[@href]");

                        if (recipeNodes == null || recipeNodes.Count == 0)
                        {
                            Console.WriteLine($"    Sayfa {page} boş, tarif bulunamadı. Atlanıp sıradaki sayfaya geçiliyor...");
                            isPageSuccessful = true;
                            break;
                        }

                        // Benzersiz tarif URL'lerini topla
                        var uniqueRecipeUrls = new HashSet<string>();
                        foreach (var linkNode in recipeNodes)
                        {
                            string recipeHref = linkNode.GetAttributeValue("href", "");
                            if (string.IsNullOrEmpty(recipeHref)) continue;
                            string recipeUrl = recipeHref.StartsWith("http") ? recipeHref : BaseUrl + recipeHref;
                            if (!scrapedUrls.Contains(recipeUrl))
                                uniqueRecipeUrls.Add(recipeUrl);
                        }

                        Console.WriteLine($"    Sayfa {page}: {recipeNodes.Count} link bulundu, {uniqueRecipeUrls.Count} yeni tarif işlenecek.");

                        int successCount = 0;
                        int failCount = 0;
                        foreach (var recipeUrl in uniqueRecipeUrls)
                        {
                            bool ok = await ScrapeRecipeDetails(client, recipeUrl, scrapedUrls);
                            if (ok) successCount++; else failCount++;
                        }

                        Console.WriteLine($"    Sayfa {page} TAMAM: {successCount} başarılı, {failCount} başarısız.");

                        isPageSuccessful = true;

                        // Sayfa başarıyla (veya boş geçilerek) tamamlandı, kaldığı yeri dosyaya yaz
                        File.WriteAllText(LastPageFilePath, (page + 1).ToString());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Liste sayfası hatası ({listPageUrl}): {ex.Message}");
                        retryCount++;
                        await Task.Delay(5000);
                    }
                }
            }

            Console.WriteLine("\nBÜTÜN SAYFALAR BAŞARIYLA TARANDI VEYA LİSTE SONUNA GELİNDİ!");
        }

        static async Task<bool> ScrapeRecipeDetails(HttpClient client, string url, HashSet<string> scrapedUrls)
        {
            int retryCount = 0;
            while (retryCount < 5)
            {
                try
                {
                    var response = await SendRequestWithRandomIpAsync(client, url);

                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        Console.WriteLine($"    [UYARI] Detay sayfasında 429 Ban! 30 saniye bekleniyor...");
                        await Task.Delay(30000);
                        retryCount++;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"    [HATA] {url} → HTTP {(int)response.StatusCode}. Tekrar denenecek ({retryCount + 1}/5)...");
                        retryCount++;
                        await Task.Delay(3000);
                        continue;
                    }

                    var html = await response.Content.ReadAsStringAsync();
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var breadcrumbNodes = doc.DocumentNode.SelectNodes("//div[@id='breadcrumb']//span[@itemprop='name']");
                    string kategori = "Genel";
                    if (breadcrumbNodes != null && breadcrumbNodes.Count > 3)
                    {
                        // İlk 2 node: "Yemek Tarifleri" ve "Tarifler", son node: yemek adı → hepsini hariç tut
                        var categoryNodes = breadcrumbNodes.Skip(2).Take(breadcrumbNodes.Count - 3).Select(n => n.InnerText.Trim());
                        string joined = string.Join(" > ", categoryNodes);
                        if (!string.IsNullOrWhiteSpace(joined))
                            kategori = joined;
                    }

                    var titleNode = doc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'recipe-name')]");
                    string baslik = titleNode != null ? titleNode.InnerText.Trim() : "Yok";

                    var kisiNode = doc.DocumentNode.SelectSingleNode("//ul[@class='short-info']//li[i[contains(@class, 'icon-knife-spoon')]]/span");
                    string kisiSayisi = kisiNode != null ? kisiNode.InnerText.Trim() : "Yok";

                    var hazirlikNode = doc.DocumentNode.SelectSingleNode("//span[@itemprop='prepTime']");
                    string hazirlikSuresi = hazirlikNode != null ? hazirlikNode.InnerText.Trim() : "Yok";

                    var pisirmeNode = doc.DocumentNode.SelectSingleNode("//span[@itemprop='cookTime']");
                    string pisirmeSuresi = pisirmeNode != null ? pisirmeNode.InnerText.Trim() : "Yok";

                    var malzemeNodes = doc.DocumentNode.SelectNodes("//li[@itemprop='recipeIngredient']");
                    string malzemeler = malzemeNodes != null ? string.Join(" | ", malzemeNodes.Select(n => n.InnerText.Trim())) : "Yok";

                    var yapilisNodes = doc.DocumentNode.SelectNodes("//ol[contains(@class, 'recipe-instructions')]/li");
                    string yapilisi = yapilisNodes != null ? string.Join(" ", yapilisNodes.Select(n => n.InnerText.Trim())) : "Yok";

                    var kaloriNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'nutrition-circle-value calories')]");
                    string kalori = kaloriNode != null ? kaloriNode.InnerText.Trim() + " kcal" : "Yok";

                    // Detaylı besin değerleri tablosu
                    var nutritionRows = doc.DocumentNode.SelectNodes("//table[@class='nutrition-table']//tr");
                    string besinDegerleri = "Yok";
                    if (nutritionRows != null)
                    {
                        var items = nutritionRows.Select(row =>
                        {
                            var cells = row.SelectNodes("td");
                            if (cells != null && cells.Count >= 2)
                                return $"{cells[0].InnerText.Trim()}: {cells[1].InnerText.Trim()}";
                            return null;
                        }).Where(x => x != null);
                        string joined = string.Join(" | ", items);
                        if (!string.IsNullOrWhiteSpace(joined))
                            besinDegerleri = joined;
                    }

                    var yzOzetNode = doc.DocumentNode.SelectSingleNode("//div[@class='owners-comment']");
                    string yapayZekaOzeti = yzOzetNode != null ? yzOzetNode.InnerText.Trim() : "Yok";

                    var ekstraNode = doc.DocumentNode.SelectSingleNode("//section[@id='additional-description']");
                    string ekstraBilgiler = ekstraNode != null ? ekstraNode.InnerText.Trim() : "Yok";

                    string csvLine = $"\"{url}\",\"{EscapeCsv(kategori)}\",\"{EscapeCsv(baslik)}\",\"{EscapeCsv(kisiSayisi)}\",\"{EscapeCsv(hazirlikSuresi)}\",\"{EscapeCsv(pisirmeSuresi)}\",\"{EscapeCsv(malzemeler)}\",\"{EscapeCsv(yapilisi)}\",\"{EscapeCsv(kalori)}\",\"{EscapeCsv(besinDegerleri)}\",\"{EscapeCsv(yapayZekaOzeti)}\",\"{EscapeCsv(ekstraBilgiler)}\"\n";

                    File.AppendAllText(CsvFilePath, csvLine, Encoding.UTF8);
                    scrapedUrls.Add(url);

                    // ── Anlık Q&A dataset üretimi ──
                    try
                    {
                        var qaRow = new Dictionary<string, string>
                        {
                            ["URL"] = url,
                            ["Kategori"] = kategori,
                            ["Baslik"] = baslik,
                            ["Kisi_Sayisi"] = kisiSayisi,
                            ["Hazirlik_Suresi"] = hazirlikSuresi,
                            ["Pisirme_Suresi"] = pisirmeSuresi,
                            ["Malzemeler"] = malzemeler,
                            ["Yapilisi"] = yapilisi,
                            ["Kalori"] = kalori,
                            ["Besin_Degerleri"] = besinDegerleri,
                            ["Yapay_Zeka_Ozeti"] = yapayZekaOzeti,
                            ["Ekstra_Bilgiler"] = ekstraBilgiler
                        };
                        QAGenerator.AppendQAForRecipe(qaRow);
                    }
                    catch (Exception qaEx)
                    {
                        Console.WriteLine($"    [QA UYARI] QA üretilemedi: {qaEx.Message}");
                    }

                    Console.WriteLine($"[EKLENDİ] {baslik}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [HATA] ({url}): {ex.Message} — Tekrar denenecek ({retryCount + 1}/5)...");
                    retryCount++;
                    await Task.Delay(3000);
                }
            }
            Console.WriteLine($"    [BAŞARISIZ] {url} — 5 denemede de eklenemedi!");
            return false;
        }

        static async Task<HttpResponseMessage> SendRequestWithRandomIpAsync(HttpClient client, string url)
        {
            string randomIp = $"{random.Next(1, 256)}.{random.Next(0, 256)}.{random.Next(0, 256)}.{random.Next(1, 256)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Forwarded-For", randomIp);
            request.Headers.Add("Client-IP", randomIp);

            return await client.SendAsync(request);
        }

        static string EscapeCsv(string text)
        {
            if (string.IsNullOrEmpty(text)) return "Yok";

            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            while (text.Contains("  "))
                text = text.Replace("  ", " ");

            return text.Replace("\"", "\"\"").Trim();
        }
    }
}