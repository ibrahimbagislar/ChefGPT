# ChefGPT Model Eğitimi

Bu klasörde ChefGPT için kullanılan Colab fine-tune notebook'u bulunur:

```text
ChefGPTModelEgitim.ipynb
```

Notebook, **Qwen2.5-14B-Instruct** modelini ChefGPT soru-cevap verisiyle LoRA yöntemi kullanarak fine-tune eder. Eğitim için Unsloth kullanılmıştır. Eğitim bittikten sonra model **GGUF** formatına çevrilir ve Google Drive'a kopyalanır. Böylece model Ollama üzerinde lokal olarak çalıştırılabilir.

## Eğitim Akışı

Notebook genel olarak şu adımları izler:

1. Colab ortamına gerekli kütüphaneleri kurar.
2. Google Drive'ı bağlar.
3. `chefgpt_qa_clean-50k.jsonl` dosyasını eğitim verisi olarak kullanır.
4. `unsloth/Qwen2.5-14B-Instruct-bnb-4bit` modelini yükler.
5. LoRA ayarlarıyla modeli eğitime hazırlar.
6. Chat template formatında dataset hazırlar.
7. `SFTTrainer` ile fine-tune yapar.
8. Checkpoint'leri Drive'a yedekler.
9. Eğitilen modeli `q4_k_m` quantization ile GGUF olarak export eder.
10. GGUF çıktısını Drive'daki `ChefGPT/chefgpt-14b-gguf` klasörüne kopyalar.

## Kullanılan Model

```text
unsloth/Qwen2.5-14B-Instruct-bnb-4bit
```

## Önemli Drive Yolları

```text
/content/drive/MyDrive/ChefGPT/chefgpt_qa_clean-50k.jsonl
/content/drive/MyDrive/ChefGPT/checkpoints
/content/drive/MyDrive/ChefGPT/chefgpt-14b-gguf
```

## Not

Eğitim verisi, checkpoint ve GGUF model dosyaları bu repository'ye eklenmedi. Bunlar büyük dosyalar olduğu için lokal/Drive ortamında tutulur.
