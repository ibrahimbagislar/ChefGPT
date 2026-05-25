# =============================================================
# chefgpt_server_faiss.py
# ChefGPT + FAISS RAG + Ollama
# Vector Search + Keyword Rerank
# =============================================================

import os
import re
import json
import time
import requests
import numpy as np
import faiss
from sentence_transformers import SentenceTransformer


# =========================
# AYARLAR
# =========================

FAISS_DIR = os.getenv("CHEFGPT_FAISS_DIR", "faiss_data")
INDEX_PATH = os.path.join(FAISS_DIR, "recipes.index")
META_PATH = os.path.join(FAISS_DIR, "recipes_meta.jsonl")

EMBEDDING_MODEL_NAME = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"

OLLAMA_URL = "http://localhost:11434/api/generate"
MODEL_NAME = os.getenv("CHEFGPT_MODEL_NAME", "chefgpt14b:latest")

VECTOR_CANDIDATES = 80
FINAL_RAG_RESULTS = 4

MAX_CONTEXT_CHARS_PER_DOC = 2600
REQUEST_TIMEOUT = 240

EMBEDDING_DEVICE = "cpu"


# =========================
# PROMPTLAR
# =========================

SOHBET_PROMPTU = """
Sen ChefGPT'sin.
Türk mutfağında uzmanlaşmış, yardımsever, samimi ve doğal konuşan bir yapay zeka aşçısısın.

Kullanıcı sadece selam verirse, hal hatır sorarsa veya seni tanımak isterse:
- Yemek tarifi verme.
- Kısa ve doğal cevap ver.
- Kendini ChefGPT olarak tanıt.
- Sonunda mutfakla ilgili nasıl yardımcı olabileceğini sor.
""".strip()


RAG_PROMPTU = """
Sen ChefGPT'sin.
Türk mutfağında uzmanlaşmış, profesyonel ama samimi bir yapay zeka aşçısısın.

Aşağıda sana FAISS vektör veritabanından çekilmiş TARİF BİLGİLERİ verilecek.
Cevabını öncelikle bu bilgilere dayandır.

Kurallar:
- Verilen TARİF BİLGİLERİ dışına gereksiz taşma.
- Uydurma tarif linki, uydurma kaynak veya uydurma URL verme.
- Eğer kullanıcı link, kaynak veya URL isterse, tarif bilgisinde geçen Kaynak URL bilgisini açıkça yaz.
- URL yoksa "Bu tarif için kaynak URL bilgisi bulunmuyor" de.
- Kullanıcı malzemeye göre tarif sorarsa, en uygun tarifleri öner.
- Kullanıcı direkt bir tarif adı sorarsa, en alakalı tarifi açıkla.
- Kullanıcı süre, kişi sayısı, kalori veya malzeme sorarsa varsa bu bilgileri belirt.
- Cevabın Türkçe, samimi, net ve uygulanabilir olsun.
- Gereksiz uzun giriş yapma.
""".strip()


# =========================
# NORMALIZATION
# =========================

TR_MAP = str.maketrans({
    "ç": "c", "Ç": "c",
    "ğ": "g", "Ğ": "g",
    "ı": "i", "I": "i",
    "İ": "i",
    "ö": "o", "Ö": "o",
    "ş": "s", "Ş": "s",
    "ü": "u", "Ü": "u",
    "â": "a", "î": "i", "û": "u",
})


STOP_WORDS = {
    "bir", "ve", "veya", "ile", "icin", "gibi", "olan", "olarak",
    "bana", "ben", "biz", "evde", "var", "yok", "mi", "mu", "mı", "mü",
    "ne", "nasil", "hangi", "neden", "acaba",
    "tarif", "tarifi", "yemek", "yemegi", "yemekleri",
    "yapilir", "yapılır", "yapabilirim", "yaparim", "yapayim",
    "oner", "onerir", "onerisi", "misin", "lazim",
    "kolay", "pratik", "guzel", "lezzetli", "nefis",
    "dakika", "dakikada", "saat", "kadar",
    "malzeme", "malzemeli", "malzemeler",
}


SUFFIXES = sorted([
    "lardan", "lerden",
    "lari", "leri",
    "lar", "ler",
    "daki", "deki", "taki", "teki",
    "dan", "den", "tan", "ten",
    "nin", "nun",
    "in", "un",
    "li", "lu",
    "siz", "suz",
    "lik", "luk",
    "ci", "cu",
    "sal", "sel",
], key=len, reverse=True)


def normalize_text(text):
    if not text:
        return ""

    text = str(text)
    text = text.translate(TR_MAP)
    text = text.lower()
    text = re.sub(r"[^a-z0-9\s]", " ", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text


def tokenize(text):
    text = normalize_text(text)
    words = re.findall(r"[a-z0-9]+", text)

    tokens = []
    for word in words:
        if len(word) < 3:
            continue

        if word in STOP_WORDS:
            continue

        tokens.append(word)

    return tokens


def stem_token(token):
    token = normalize_text(token)

    if len(token) <= 4:
        return token

    for suffix in SUFFIXES:
        suffix = normalize_text(suffix)

        if token.endswith(suffix) and len(token) > len(suffix) + 2:
            root = token[:-len(suffix)]

            if len(root) >= 3:
                return root

    return token


def expand_query_terms(question):
    base_tokens = tokenize(question)
    expanded = set()

    for token in base_tokens:
        expanded.add(token)

        root = stem_token(token)
        if root and root != token:
            expanded.add(root)

    return sorted(expanded)


# =========================
# SOHBET KONTROL
# =========================

def is_sohbet(soru):
    soru_temiz = normalize_text(soru.strip())

    sohbet_kelimeleri = {
        "merhaba", "selam", "naber", "nasilsin", "kimsin",
        "adin ne", "gunaydin", "iyi aksamlar", "iyi geceler"
    }

    yemek_kelimeleri = {
        "tarif", "yemek", "corba", "tatli", "kek", "kurabiye",
        "pasta", "borek", "tavuk", "et", "kiyma", "patlican",
        "domates", "makarna", "pilav", "malzeme", "pisir",
        "firin", "kalori", "link", "url", "kaynak"
    }

    if any(k in soru_temiz for k in yemek_kelimeleri):
        return False

    if len(soru_temiz.split()) <= 5 and any(k in soru_temiz for k in sohbet_kelimeleri):
        return True

    return False


# =========================
# DATA LOAD
# =========================

def load_metadata():
    metadata = []

    with open(META_PATH, "r", encoding="utf-8") as f:
        for line in f:
            if line.strip():
                metadata.append(json.loads(line))

    return metadata


# =========================
# SCORING
# =========================

def count_occurrences(text, term):
    if not text or not term:
        return 0

    return len(re.findall(rf"\b{re.escape(term)}\b", text))


def keyword_rerank_score(question, item):
    terms = expand_query_terms(question)
    phrase = normalize_text(question)

    title = normalize_text(item.get("baslik", ""))
    category = normalize_text(item.get("kategori", ""))
    url = normalize_text(item.get("url", ""))
    document = normalize_text(item.get("document", ""))

    score = 0.0

    if phrase and len(phrase) >= 3:
        if phrase in title:
            score += 30
        if phrase in category:
            score += 12
        if phrase in document:
            score += 10

    for term in terms:
        title_count = count_occurrences(title, term)
        category_count = count_occurrences(category, term)
        doc_count = count_occurrences(document, term)
        url_count = count_occurrences(url, term)

        if title_count:
            score += 14 * title_count

        if category_count:
            score += 7 * category_count

        if doc_count:
            score += min(12, 3 * doc_count)

        if url_count:
            score += 1 * url_count

    if terms:
        full_text = f"{title} {category} {document}"
        matched = sum(1 for term in terms if term in full_text)
        coverage = matched / len(terms)
        score += coverage * 10

    return score


def final_score(vector_score, keyword_score):
    return keyword_score + (float(vector_score) * 4)


# =========================
# SEARCH
# =========================

def hybrid_search(index, metadata, embed_model, question):
    query_vector = embed_model.encode(
        [question],
        normalize_embeddings=True,
        convert_to_numpy=True
    ).astype("float32")

    scores, ids = index.search(query_vector, VECTOR_CANDIDATES)

    items = []

    for vector_score, idx in zip(scores[0], ids[0]):
        if idx < 0 or idx >= len(metadata):
            continue

        item = metadata[idx]
        k_score = keyword_rerank_score(question, item)
        f_score = final_score(vector_score, k_score)

        items.append({
            "faiss_id": int(idx),
            "vector_score": float(vector_score),
            "keyword_score": float(k_score),
            "final_score": float(f_score),
            "item": item
        })

    items.sort(key=lambda x: x["final_score"], reverse=True)
    return items[:FINAL_RAG_RESULTS]


def build_context(results):
    blocks = []

    for i, result in enumerate(results, start=1):
        item = result["item"]
        doc = item.get("document", "")[:MAX_CONTEXT_CHARS_PER_DOC]

        block = f"""
--- TARİF {i} ---
Başlık: {item.get("baslik", "")}
Kategori: {item.get("kategori", "")}
Kişi Sayısı: {item.get("kisi_sayisi", "")}
Hazırlık Süresi: {item.get("hazirlik_suresi", "")}
Pişirme Süresi: {item.get("pisirme_suresi", "")}
Kalori: {item.get("kalori", "")}
Kaynak URL: {item.get("url", "")}
Vector Skor: {result["vector_score"]:.4f}
Keyword Skor: {result["keyword_score"]:.2f}
Final Skor: {result["final_score"]:.2f}

İçerik:
{doc}
""".strip()

        blocks.append(block)

    return "\n\n".join(blocks)


# =========================
# OLLAMA
# =========================

def ollama_generate(prompt):
    payload = {
        "model": MODEL_NAME,
        "prompt": prompt,
        "stream": False,
        "options": {
            "temperature": 0.45,
            "top_p": 0.9,
            "num_ctx": 8192,
            "num_predict": 900,
            "repeat_penalty": 1.08
        }
    }

    response = requests.post(
        OLLAMA_URL,
        json=payload,
        timeout=REQUEST_TIMEOUT
    )

    response.raise_for_status()
    return response.json().get("response", "").strip()


def rag_query(question, index, metadata, embed_model):
    question = question.strip()

    if not question:
        return "Bir soru yazarsan yardımcı olayım."

    if is_sohbet(question):
        prompt = f"""
{SOHBET_PROMPTU}

Kullanıcı:
{question}

ChefGPT:
""".strip()

        return ollama_generate(prompt)

    results = hybrid_search(index, metadata, embed_model, question)
    context = build_context(results)

    prompt = f"""
{RAG_PROMPTU}

=== TARİF BİLGİLERİ ===
{context}

=== KULLANICI SORUSU ===
{question}

=== CEVAP ===
""".strip()

    return ollama_generate(prompt)


# =========================
# MAIN
# =========================

def main():
    if not os.path.exists(INDEX_PATH):
        raise FileNotFoundError(f"FAISS index bulunamadı: {INDEX_PATH}")

    if not os.path.exists(META_PATH):
        raise FileNotFoundError(f"Metadata bulunamadı: {META_PATH}")

    print("FAISS index yükleniyor...")
    index = faiss.read_index(INDEX_PATH)

    print("Metadata yükleniyor...")
    metadata = load_metadata()

    print("Embedding modeli yükleniyor...")
    embed_model = SentenceTransformer(
        EMBEDDING_MODEL_NAME,
        device=EMBEDDING_DEVICE
    )

    print(f"Index count   : {index.ntotal:,}")
    print(f"Metadata count: {len(metadata):,}")

    if index.ntotal != len(metadata):
        print("UYARI: FAISS index sayısı ile metadata sayısı eşit değil.")

    print(f"Ollama model adı: {MODEL_NAME}")
    print("\nChefGPT FAISS RAG hazır. Çıkış için q yaz.\n")

    while True:
        question = input("Sen: ").strip()

        if question.lower() in ("q", "quit", "exit", "çıkış", "cikis"):
            print("Görüşürüz.")
            break

        if not question:
            continue

        try:
            start = time.time()
            answer = rag_query(question, index, metadata, embed_model)
            elapsed = time.time() - start

            print(f"\nChefGPT:\n{answer}")
            print(f"\nYanıt süresi: {elapsed:.2f} sn\n")

        except requests.exceptions.ConnectionError:
            print("\nHATA: Ollama çalışmıyor olabilir.")
            print("Kontrol:")
            print("ollama list")
            print("ollama run chefgpt\n")

        except Exception as e:
            print(f"\nHATA: {e}\n")


if __name__ == "__main__":
    main()
