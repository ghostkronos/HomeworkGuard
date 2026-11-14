using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Models;
using GenAiType = Google.GenAI.Types.Type;

namespace HomeworkGuard.Service.AI;

/// <summary>
/// Two-stage anti-cheat homework evaluator powered by Google Gemini (gemini-3.5-flash-lite)
/// with Multimodal input and Structured JSON Outputs.
/// </summary>
public sealed class GeminiHomeworkEvaluator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Schema Stage1Schema = new()
    {
        Type = GenAiType.Object,
        Title = "HomeworkStage1Evaluation",
        Properties = new Dictionary<string, Schema>
        {
            ["passedStage1"] = new Schema { Type = GenAiType.Boolean, Description = "Ödev kriterleri ve görsel/içerik yeterliliği ilk aşamayı geçti mi?" },
            ["feedback"] = new Schema { Type = GenAiType.String, Description = "Öğrenciye Türkçe olarak açık, yapıcı geri bildirim." },
            ["detectedSummary"] = new Schema { Type = GenAiType.String, Description = "Gönderilen ödevde tespit edilen içerik, soru sayısı ve çözüm özeti." },
            ["socraticQuestions"] = new Schema
            {
                Type = GenAiType.Array,
                Items = new Schema { Type = GenAiType.String },
                Description = "Ödev içeriğinden seçilen tam olarak 2 adet spesifik Sokratik doğrulama sorusu."
            }
        },
        PropertyOrdering = ["passedStage1", "feedback", "detectedSummary", "socraticQuestions"],
        Required = ["passedStage1", "feedback", "detectedSummary", "socraticQuestions"]
    };

    private static readonly Schema Stage2Schema = new()
    {
        Type = GenAiType.Object,
        Title = "HomeworkStage2Verification",
        Properties = new Dictionary<string, Schema>
        {
            ["approved"] = new Schema { Type = GenAiType.Boolean, Description = "Öğrencinin Sokratik sorulara verdiği cevaplar ödevi gerçekten kendisinin yaptığını doğruluyor mu?" },
            ["feedback"] = new Schema { Type = GenAiType.String, Description = "Öğrenciye Türkçe değerlendirme geri bildirimi." },
            ["score"] = new Schema { Type = GenAiType.Integer, Description = "0 ile 100 arasında başarı puanı." }
        },
        PropertyOrdering = ["approved", "feedback", "score"],
        Required = ["approved", "feedback", "score"]
    };

    public async Task<GeminiStage1Result> EvaluateStage1Async(
        string apiKey,
        string dailyCriteria,
        byte[] fileBytes,
        string mimeType,
        string fileName,
        string studentNote,
        CancellationToken ct,
        string? modelName = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new GeminiStage1Result
            {
                PassedStage1 = false,
                Feedback = "Gemini API anahtarı yapılandırılmamış. Lütfen yönetici panelinden API anahtarını girin."
            };
        }

        var client = new Client(apiKey: apiKey);
        string resolvedModel = string.IsNullOrWhiteSpace(modelName) ? AppBuildConfig.GeminiModelName : modelName.Trim();

        string systemPrompt = """
            Sen sıkı ama adil bir Yapay Zeka Ödev Denetmenisin. Görevin öğrencinin gönderdiği ödevi analiz edip kopya, boş kağıt veya eksik çalışma girişimlerini tespit etmektir.
            Kurallar:
            1. Boş kağıt, okunaksız fotoğraf, alakasız ekran görüntüsü veya yalnızca soruları içeren (çözüm/işlem adımı olmayan) gönderileri KESİNLİKLE reddet (passedStage1 = false).
            2. Yöneticinin belirlediği günlük ödev kriterini tam kontrol et. Kriter karşılanmıyorsa reddet ve eksik kalan noktaları Türkçe olarak açıkla.
            3. Eğer çalışma gerçek, okunabilir ve kriteri karşılıyorsa passedStage1 = true döndür ve ödevin içindeki belirli soru numaralarına/adımlarına/satırlarına atıfta bulunan TAM OLARAK 2 adet spesifik Sokratik soru üret (örneğin: "4. soruda payda eşitlemeyi neden bu şekilde yaptın?" veya "Yazdığın döngüde i=0 yerine i=1 başlatsaydın sonuç nasıl değişirdi?").
            4. Tüm yanıtların Türkçe olmalıdır.
            """;

        string userPrompt = $"""
            Günlük Yönetici Ödev Kriteri: {dailyCriteria}
            Dosya Adı: {fileName}
            Öğrenci Notu: {(string.IsNullOrWhiteSpace(studentNote) ? "Yok" : studentNote)}

            Lütfen ekteki ödev dosyasını dikkatle incele ve ilk aşama değerlendirmesini yap.
            """;

        var parts = BuildMediaAndPromptParts(fileBytes, mimeType, fileName, userPrompt);

        string json = await GenerateStructuredJsonAsync(
            client, resolvedModel, systemPrompt, parts, Stage1Schema, ct);

        var parsed = JsonSerializer.Deserialize<GeminiStage1Result>(json, JsonOpts)
            ?? throw new InvalidOperationException("Gemini Stage 1 JSON ayrıştırılamadı.");

        if (parsed.PassedStage1 && parsed.SocraticQuestions.Count < 2)
        {
            while (parsed.SocraticQuestions.Count < 2)
            {
                parsed.SocraticQuestions.Add("Ödevindeki çözüm adımlarından birini kendi cümlelerinle kısaca açıklar mısın?");
            }
        }
        else if (parsed.SocraticQuestions.Count > 2)
        {
            parsed.SocraticQuestions = parsed.SocraticQuestions.Take(2).ToList();
        }

        return parsed;
    }

    public async Task<GeminiStage2Result> EvaluateStage2Async(
        string apiKey,
        string dailyCriteria,
        byte[] fileBytes,
        string mimeType,
        string fileName,
        string stage1Summary,
        IReadOnlyList<string> questions,
        string answer1,
        string answer2,
        CancellationToken ct,
        string? modelName = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new GeminiStage2Result
            {
                Approved = false,
                Feedback = "Gemini API anahtarı yapılandırılmamış.",
                Score = 0
            };
        }

        var client = new Client(apiKey: apiKey);
        string resolvedModel = string.IsNullOrWhiteSpace(modelName) ? AppBuildConfig.GeminiModelName : modelName.Trim();

        string systemPrompt = """
            Sen bir Yapay Zeka Ödev Doğrulama Denetmenisin. İlk aşamayı geçen öğrencinin kendi ödevine ait Sokratik sorulara verdiği yanıtları değerlendiriyorsun.
            Kurallar:
            1. Öğrencinin cevaplarının yüzeysel, kopyala-yapıştır, anlamsız veya ödevdeki gerçek çözümlerle tutarsız olup olmadığını kontrol et.
            2. Öğrenci her iki soruyu da kendi çalışmasını anladığını gösterecek şekilde mantıklı ve tutarlı biçimde yanıtladıysa approved = true ve adil bir score (0-100) ver.
            3. Cevaplar tutarsız, aşırı kısa/geçiştirilmiş veya yanlışsa approved = false ver ve neleri eksik/yanlış açıkladığını Türkçe bildir.
            """;

        string q1 = questions.Count > 0 ? questions[0] : "Soru 1";
        string q2 = questions.Count > 1 ? questions[1] : "Soru 2";

        string userPrompt = $"""
            Günlük Ödev Kriteri: {dailyCriteria}
            Aşama 1 Özeti: {stage1Summary}

            Sokratik Soru 1: {q1}
            Öğrenci Cevabı 1: {answer1}

            Sokratik Soru 2: {q2}
            Öğrenci Cevabı 2: {answer2}

            Ekteki ödev içeriğiyle karşılaştırarak nihai onay kararını ver.
            """;

        var parts = BuildMediaAndPromptParts(fileBytes, mimeType, fileName, userPrompt);

        string json = await GenerateStructuredJsonAsync(
            client, resolvedModel, systemPrompt, parts, Stage2Schema, ct);

        var parsed = JsonSerializer.Deserialize<GeminiStage2Result>(json, JsonOpts)
            ?? throw new InvalidOperationException("Gemini Stage 2 JSON ayrıştırılamadı.");

        parsed.Score = Math.Clamp(parsed.Score, 0, 100);
        return parsed;
    }

    private static async Task<string> GenerateStructuredJsonAsync(
        Client client,
        string primaryModel,
        string systemPrompt,
        List<Part> parts,
        Schema schema,
        CancellationToken ct)
    {
        var candidateModels = new[]
        {
            primaryModel,
            "gemini-3.5-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.6-flash"
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Exception? lastEx = null;
        foreach (string model in candidateModels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var callTask = client.Models.GenerateContentAsync(
                    model: model,
                    contents: new List<Content> { new() { Role = "user", Parts = parts } },
                    config: new GenerateContentConfig
                    {
                        SystemInstruction = new Content { Parts = [new Part { Text = systemPrompt }] },
                        ResponseMimeType = "application/json",
                        ResponseSchema = schema
                    });

                var response = await callTask.WaitAsync(TimeSpan.FromSeconds(22), ct);

                string? json = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return json;
                }

                lastEx = new InvalidOperationException($"Gemini API ({model}) boş yanıt döndürdü.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastEx = new TimeoutException($"Gemini API ({model}) zaman aşımına uğradı.");
            }
            catch (Exception ex) when (IsFailoverGeminiError(ex))
            {
                lastEx = ex;
                await Task.Delay(400, ct);
            }
        }

        throw lastEx ?? new InvalidOperationException("Gemini API yanıt vermedi.");
    }

    private static bool IsFailoverGeminiError(Exception ex)
    {
        if (ex is TimeoutException or HttpRequestException) return true;
        string msg = ex.Message;
        return msg.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("404", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Part> BuildMediaAndPromptParts(byte[] fileBytes, string mimeType, string fileName, string promptText)
    {
        var parts = new List<Part>();

        if (IsTextLikeMimeOrExtension(mimeType, fileName))
        {
            string codeContent = Encoding.UTF8.GetString(fileBytes);
            if (codeContent.Length > 40_000)
            {
                codeContent = codeContent[..40_000] + "\n...[içerik kısaltıldı]";
            }

            parts.Add(new Part
            {
                Text = $"--- Gönderilen Dosya ({fileName}) ---\n{codeContent}\n--- Dosya Sonu ---"
            });
        }
        else
        {
            parts.Add(new Part
            {
                InlineData = new Blob
                {
                    MimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType,
                    Data = fileBytes
                }
            });
        }

        parts.Add(new Part { Text = promptText });
        return parts;
    }

    private static bool IsTextLikeMimeOrExtension(string mimeType, string fileName)
    {
        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".txt" or ".cs" or ".py" or ".js" or ".ts" or ".cpp" or ".c" or ".java" or ".md" or ".json" or ".html" or ".css";
    }
}
