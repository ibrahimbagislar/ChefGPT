# ChefGPT

ChefGPT, Türk mutfağı tarifleri üzerinde çalışan lokal bir yapay zeka yemek asistanı projesidir. Proje; web scraping ile tarif verisi toplama, tariflerden soru-cevap verisi üretme, Qwen2.5-14B modelini ChefGPT verisiyle fine-tune etme, FAISS tabanlı semantic search ve Ollama ile çalışan RAG akışını bir araya getirir.

Bu repository'de veri dosyaları paylaşılmadı. Büyük CSV, JSONL, FAISS index ve model dosyaları lokal olarak üretilir veya kullanıcı tarafından sağlanır.

## Özellikler

- Nefis Yemek Tarifleri üzerinden tarif scraping kodu
- Tarif verisinden ChefGPT için Q&A dataset üretimi
- Unsloth ile Qwen2.5-14B-Instruct fine-tune notebook'u
- GGUF export ile Ollama üzerinde lokal model çalıştırma
- FAISS ile hızlı tarif arama
- Sentence Transformers ile Türkçe destekli embedding
- Ollama üzerinde çalışan lokal LLM bağlantısı
- RAG akışı: kullanıcı sorusu → FAISS arama → context oluşturma → Ollama cevabı
- Basit ve şık web arayüzü

## Proje Yapısı

```text
ChefGPT/
├── scraper/
│   ├── Program.cs
│   ├── QAGenerator.cs
│   └── ChefGPTScraper.csproj
├── training/
│   ├── ChefGPTModelEgitim.ipynb
│   └── README.md
├── local/
│   ├── build_faiss.py
│   ├── chefgpt_server_faiss.py
│   └── requirements.txt
├── website/
│   ├── index.html
│   ├── app.js
│   └── style.css
├── .gitignore
└── README.md
```

## Repository'de Neler Yok?

Aşağıdaki dosyalar özellikle eklenmedi:

- Scraping sonucu oluşan CSV dosyaları
- Q&A dataset dosyaları
- Fine-tune checkpoint dosyaları
- FAISS index ve metadata çıktıları
- ChromaDB çıktıları
- GGUF/model dosyaları
- `bin`, `obj`, `.vs` gibi build/debug klasörleri
- Kişisel çalışma notları ve rehber dosyaları

Bu dosyalar büyük veya lokal ortama özel olduğu için `.gitignore` ile dışarıda bırakıldı.

## Gereksinimler

### .NET Scraper

- .NET 8 SDK
- HtmlAgilityPack

Kurulum:

```powershell
cd scraper
dotnet restore
```

### Lokal RAG / FAISS

- Python 3.10+
- Ollama
- FAISS
- Sentence Transformers

Python paketleri:

```powershell
cd local
pip install -r requirements.txt
```

## Veri Akışı

Genel çalışma akışı şu şekilde:

1. `scraper/Program.cs` tarifleri toplar ve CSV üretir.
2. `scraper/QAGenerator.cs` tariflerden soru-cevap formatında veri üretir.
3. `training/ChefGPTModelEgitim.ipynb` Qwen2.5-14B-Instruct modelini ChefGPT verisiyle fine-tune eder.
4. Fine-tune edilen model GGUF olarak export edilir ve Ollama üzerinde çalıştırılır.
5. `local/build_faiss.py` CSV tarif verisini okuyup FAISS index oluşturur.
6. `local/chefgpt_server_faiss.py` FAISS index + Ollama ile lokal ChefGPT RAG akışını çalıştırır.
7. `website/` klasöründeki arayüz kullanıcıdan soru alır ve backend'e gönderir.

## Model Eğitimi

Model eğitimi için kullanılan notebook:

```text
training/ChefGPTModelEgitim.ipynb
```

Bu notebook Colab üzerinde çalışacak şekilde hazırlanmıştır. Eğitimde kullanılan ana model:

```text
unsloth/Qwen2.5-14B-Instruct-bnb-4bit
```

Notebook'taki ana adımlar:

- Unsloth, TRL, PEFT, bitsandbytes ve datasets kurulumları yapılır.
- Google Drive bağlanır.
- `chefgpt_qa_clean-50k.jsonl` eğitim verisi okunur.
- Qwen2.5-14B-Instruct 4-bit olarak yüklenir.
- LoRA ayarlarıyla fine-tune yapılır.
- Checkpoint'ler Drive'a kaydedilir.
- Eğitim bitince model `q4_k_m` quantization ile GGUF formatına export edilir.
- GGUF model Drive'daki `ChefGPT/chefgpt-14b-gguf` klasörüne kopyalanır.

Eğitim çıktıları ve GGUF dosyası büyük olduğu için repository'ye eklenmedi.

## FAISS Index Oluşturma

CSV dosyası repository'de yoktur. Kendi CSV dosyanızı `data/nefis_yemek_tarifleri_dataset.csv` yoluna koyabilirsiniz.

Alternatif olarak environment variable ile CSV yolunu verebilirsiniz:

```powershell
$env:CHEFGPT_CSV_PATH="C:\path\to\nefis_yemek_tarifleri_dataset.csv"
$env:CHEFGPT_FAISS_DIR="C:\ChefGPT\faiss_data"
python local\build_faiss.py
```

Varsayılan ayarlar:

- CSV yolu: `data/nefis_yemek_tarifleri_dataset.csv`
- FAISS çıktı klasörü: `faiss_data`

`build_faiss.py` çalışınca şunlar üretilir:

```text
faiss_data/
├── recipes.index
└── recipes_meta.jsonl
```

Bu klasör GitHub'a eklenmez.

## ChefGPT RAG Sunucusu

Önce Ollama tarafında modelinizin hazır olması gerekir. Varsayılan model adı:

```text
chefgpt14b:latest
```

Farklı model adı kullanacaksanız:

```powershell
$env:CHEFGPT_MODEL_NAME="chefgpt"
$env:CHEFGPT_FAISS_DIR="C:\ChefGPT\faiss_data"
python local\chefgpt_server_faiss.py
```

Sunucu terminal üzerinde interaktif çalışır. Örnek:

```text
Sen: Evde tavuk ve patates var, ne yapabilirim?
ChefGPT: ...
```

## Website

`website/` klasörü, ChefGPT için hazırlanmış frontend arayüzüdür.

Arayüz `/api/health` ve `/api/chat` endpoint'lerini bekler. Bu nedenle production kullanımda bir backend ya da proxy ile bağlanması gerekir.

Basit geliştirme için statik dosyaları açabilirsiniz:

```powershell
cd website
python -m http.server 8080
```

Sonra:

```text
http://localhost:8080
```

## Notlar

- Bu proje lokal çalışmaya göre tasarlanmıştır.
- Model ve veri dosyaları büyük olduğu için repository dışında tutulur.
- FAISS index yeniden üretilebilir olduğu için kaynak veri ve script yeterlidir.
- Ollama'nın arka planda çalışıyor olması gerekir.

## Kullanılan Teknolojiler

- C# / .NET 8
- HtmlAgilityPack
- Python
- FAISS
- Sentence Transformers
- Ollama
- HTML / CSS / JavaScript
