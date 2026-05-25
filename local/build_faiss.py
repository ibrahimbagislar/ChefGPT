# =============================================================
# build_faiss.py
# CSV tarif verilerini FAISS vector index'e yükler
# Metadata ayrı JSONL dosyasında tutulur
# =============================================================

import os
import json
import time
import shutil
import pandas as pd
import numpy as np
import faiss
from sentence_transformers import SentenceTransformer

# =========================
# AYARLAR
# =========================

CSV_PATH = os.getenv("CHEFGPT_CSV_PATH", "data/nefis_yemek_tarifleri_dataset.csv")

FAISS_DIR = os.getenv("CHEFGPT_FAISS_DIR", "faiss_data")
INDEX_PATH = os.path.join(FAISS_DIR, "recipes.index")
META_PATH = os.path.join(FAISS_DIR, "recipes_meta.jsonl")

EMBEDDING_MODEL_NAME = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"

CSV_CHUNK_SIZE = 5000
EMBED_BATCH_SIZE = 128

RESET_DB = True


# =========================
# TEMİZLEME
# =========================

def clean_text(value):
    if value is None:
        return ""

    text = str(value)

    if text.lower() in ("nan", "none", "null"):
        return ""

    text = (
        text.replace("\u00A0", " ")
        .replace("\r\n", "\n")
        .replace("\r", "\n")
        .strip()
    )

    while "  " in text:
        text = text.replace("  ", " ")

    return text


def val_or_none(value):
    text = clean_text(value)

    if not text:
        return None

    if text.lower() in ("yok", "null", "none", "-", "nan", "boş", "bos"):
        return None

    return text


def clean_malzemeler(raw):
    text = val_or_none(raw)

    if not text:
        return ""

    parts = [x.strip() for x in text.split("|") if x.strip()]
    return ", ".join(parts)


def short_malzemeler(raw, limit=12):
    text = val_or_none(raw)

    if not text:
        return ""

    parts = [x.strip() for x in text.split("|") if x.strip()]
    return ", ".join(parts[:limit])


# =========================
# DOCUMENT / EMBEDDING
# =========================

def build_document(row):
    url = val_or_none(row.get("URL"))
    kategori = val_or_none(row.get("Kategori"))
    baslik = val_or_none(row.get("Baslik")) or ""
    kisi = val_or_none(row.get("Kisi_Sayisi"))
    hazirlik = val_or_none(row.get("Hazirlik_Suresi"))
    pisirme = val_or_none(row.get("Pisirme_Suresi"))
    malzemeler = clean_malzemeler(row.get("Malzemeler"))
    yapilisi = val_or_none(row.get("Yapilisi"))
    kalori = val_or_none(row.get("Kalori"))
    besin = val_or_none(row.get("Besin_Degerleri"))
    ekstra = val_or_none(row.get("Ekstra_Bilgiler"))

    parts = []

    parts.append(f"Tarif Adı: {baslik}.")

    if kategori:
        parts.append(f"Kategori: {kategori}.")

    if kisi:
        parts.append(f"Kişi Sayısı: {kisi}.")

    if hazirlik:
        parts.append(f"Hazırlık Süresi: {hazirlik}.")

    if pisirme:
        parts.append(f"Pişirme Süresi: {pisirme}.")

    if malzemeler:
        parts.append(f"Malzemeler: {malzemeler}.")

    if yapilisi:
        parts.append(f"Yapılışı: {yapilisi}")

    if kalori:
        parts.append(f"Kalori: {kalori}.")

    if besin:
        parts.append(f"Besin Değerleri: {besin}.")

    if ekstra:
        parts.append(f"Ekstra Bilgiler: {ekstra}.")

    if url:
        parts.append(f"Kaynak URL: {url}")

    return " ".join(parts)


def build_embedding_text(row):
    baslik = val_or_none(row.get("Baslik")) or ""
    kategori = val_or_none(row.get("Kategori")) or ""
    kisi = val_or_none(row.get("Kisi_Sayisi")) or ""
    hazirlik = val_or_none(row.get("Hazirlik_Suresi")) or ""
    pisirme = val_or_none(row.get("Pisirme_Suresi")) or ""
    malzeme_ozet = short_malzemeler(row.get("Malzemeler"), limit=12)

    # Başlığı iki kez vermek direkt tarif adı aramalarını güçlendirir
    return (
        f"Tarif adı: {baslik}. "
        f"Tarif adı tekrar: {baslik}. "
        f"Kategori: {kategori}. "
        f"Kişi: {kisi}. "
        f"Hazırlık: {hazirlik}. "
        f"Pişirme: {pisirme}. "
        f"Ana malzemeler: {malzeme_ozet}. "
        f"Malzemeler: {malzeme_ozet}."
    )


def build_metadata(row, row_id):
    url = val_or_none(row.get("URL")) or ""
    kategori = val_or_none(row.get("Kategori")) or "Genel"
    baslik = val_or_none(row.get("Baslik")) or ""
    kisi = val_or_none(row.get("Kisi_Sayisi")) or ""
    hazirlik = val_or_none(row.get("Hazirlik_Suresi")) or ""
    pisirme = val_or_none(row.get("Pisirme_Suresi")) or ""
    kalori = val_or_none(row.get("Kalori")) or ""
    doc = build_document(row)

    return {
        "id": row_id,
        "baslik": baslik,
        "kategori": kategori,
        "url": url,
        "kisi_sayisi": kisi,
        "hazirlik_suresi": hazirlik,
        "pisirme_suresi": pisirme,
        "kalori": kalori,
        "document": doc
    }


# =========================
# MAIN
# =========================

def main():
    if not os.path.exists(CSV_PATH):
        raise FileNotFoundError(f"CSV bulunamadı: {CSV_PATH}")

    if RESET_DB and os.path.exists(FAISS_DIR):
        print(f"Eski FAISS klasörü siliniyor: {FAISS_DIR}")
        shutil.rmtree(FAISS_DIR)

    os.makedirs(FAISS_DIR, exist_ok=True)

    print("Embedding modeli yükleniyor...")

    try:
        embed_model = SentenceTransformer(EMBEDDING_MODEL_NAME, device="cuda")
        print("Embedding device: CUDA")
    except Exception:
        embed_model = SentenceTransformer(EMBEDDING_MODEL_NAME, device="cpu")
        print("Embedding device: CPU")

    index = None
    total = 0
    skipped = 0
    errors = 0
    start_time = time.time()

    print("\nCSV okunuyor ve FAISS index oluşturuluyor...\n")

    with open(META_PATH, "w", encoding="utf-8") as meta_file:
        for chunk_index, df in enumerate(
            pd.read_csv(
                CSV_PATH,
                chunksize=CSV_CHUNK_SIZE,
                encoding="utf-8-sig",
                dtype=str,
                keep_default_na=False
            )
        ):
            embed_texts = []
            meta_items = []

            for row_index, row in df.iterrows():
                try:
                    baslik = val_or_none(row.get("Baslik"))

                    if not baslik:
                        skipped += 1
                        continue

                    row_id = total + len(embed_texts)

                    embed_text = build_embedding_text(row)
                    metadata = build_metadata(row, row_id)

                    embed_texts.append(embed_text)
                    meta_items.append(metadata)

                except Exception as e:
                    errors += 1
                    if errors <= 10:
                        print(f"Hata: {e}")

            if not embed_texts:
                continue

            embeddings = embed_model.encode(
                embed_texts,
                batch_size=EMBED_BATCH_SIZE,
                show_progress_bar=False,
                normalize_embeddings=True,
                convert_to_numpy=True
            ).astype("float32")

            if index is None:
                dim = embeddings.shape[1]
                index = faiss.IndexFlatIP(dim)

            index.add(embeddings)

            for item in meta_items:
                meta_file.write(json.dumps(item, ensure_ascii=False) + "\n")

            total += len(embed_texts)

            elapsed = time.time() - start_time
            speed = total / elapsed if elapsed > 0 else 0

            print(f"{total:>8,} tarif eklendi | hız: {speed:.1f} tarif/sn")

    if index is None:
        raise RuntimeError("Hiç embedding oluşturulamadı.")

    print("\nFAISS index diske yazılıyor...")
    faiss.write_index(index, INDEX_PATH)

    print("\n===================================================")
    print("TAMAMLANDI")
    print(f"Toplam eklenen : {total:,}")
    print(f"Atlanan        : {skipped:,}")
    print(f"Hata           : {errors:,}")
    print(f"Index path     : {INDEX_PATH}")
    print(f"Metadata path  : {META_PATH}")
    print(f"FAISS count    : {index.ntotal:,}")
    print("===================================================")


if __name__ == "__main__":
    main()
