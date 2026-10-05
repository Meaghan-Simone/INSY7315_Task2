using System.Globalization;
using System.Text;
using System.Text.Json;
using UncoveringGreatnessCRM.Helpers;

namespace UncoveringGreatnessCRM.Services;

public record FormAnswer(string Key, string Label, string Type, string Value);

public class ParsedSubmission
{
    public string? ExternalId { get; set; }
    public string? FormName { get; set; }
    public List<FormAnswer> Answers { get; } = new();
}

public class MappedLead
{
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Unmapped { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Get(string field) => Fields.TryGetValue(field, out var v) && v.Length > 0 ? v : null;
}

/// <summary>
/// Understands the JSON that form tools send and turns it into lead fields.
/// Supported shapes: Tally webhooks (data.fields[] with label/type/value/options), a generic { "fields": [ {label,value} ] },
/// { "fields": { "Question": "Answer" } } and a flat { "Question": "Answer" } object (Zapier, Make, Apps Script, curl...).
/// </summary>
public static class FormPayloadParser
{
    public static readonly string[] FieldNames =
        { "FirstName", "Surname", "FullName", "Email", "Phone", "JobTitle", "Company", "Industry", "City", "Province", "LinkedIn", "Message" };

    private static readonly HashSet<string> MetaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "responseId", "submissionId", "id", "eventId", "eventType", "createdAt", "submittedAt", "formId", "formName", "respondentId", "source", "data", "fields"
    };

    // ------------------------------------------------------------------ parsing
    public static ParsedSubmission Parse(JsonElement root)
    {
        var result = new ParsedSubmission();
        var data = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) data = d;
        if (data.ValueKind != JsonValueKind.Object) return result;

        result.ExternalId = Trunc(Str(data, "responseId") ?? Str(data, "submissionId") ?? Str(data, "id")
                                  ?? Str(root, "responseId") ?? Str(root, "submissionId") ?? Str(root, "id") ?? Str(root, "eventId"), 120);
        result.FormName = Trunc(Str(data, "formName") ?? Str(root, "formName"), 200);

        if (data.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fields.EnumerateArray()) AddField(result, item);
        }
        else if (data.TryGetProperty("fields", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject()) result.Answers.Add(new FormAnswer(p.Name, p.Name, "", Stringify(p.Value, null)));
        }
        else
        {
            foreach (var p in data.EnumerateObject())
            {
                if (MetaKeys.Contains(p.Name) || p.Value.ValueKind == JsonValueKind.Object) continue;
                result.Answers.Add(new FormAnswer(p.Name, p.Name, "", Stringify(p.Value, null)));
            }
        }
        return result;
    }

    private static void AddField(ParsedSubmission r, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        var label = Str(item, "label") ?? Str(item, "title") ?? Str(item, "question") ?? Str(item, "name") ?? Str(item, "key") ?? "";
        var key = Str(item, "key") ?? label;
        var type = Str(item, "type") ?? "";
        JsonElement value = default; var has = false;
        foreach (var n in new[] { "value", "answer", "response" })
            if (item.TryGetProperty(n, out value)) { has = true; break; }
        Dictionary<string, string>? options = null;
        if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            options = new Dictionary<string, string>();
            foreach (var o in opts.EnumerateArray())
            {
                var id = Str(o, "id"); var text = Str(o, "text") ?? Str(o, "name") ?? Str(o, "label");
                if (id != null && text != null) options[id] = text;
            }
        }
        r.Answers.Add(new FormAnswer(key, label, type, has ? Stringify(value, options) : ""));
    }

    private static string Stringify(JsonElement v, Dictionary<string, string>? options)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString() ?? "";
                return Trunc((options != null && options.TryGetValue(s, out var mapped) ? mapped : s).Trim(), 2000)!;
            case JsonValueKind.Number: return v.GetRawText();
            case JsonValueKind.True: return "Yes";
            case JsonValueKind.False: return "No";
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var e in v.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object) parts.Add(Str(e, "name") ?? Str(e, "text") ?? Str(e, "label") ?? Str(e, "url") ?? "");
                    else parts.Add(Stringify(e, options));
                }
                return Trunc(string.Join(", ", parts.Where(p => p.Length > 0)), 2000)!;
            default: return "";
        }
    }

    private static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(p.GetString()) ? null : p.GetString()!.Trim(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null
        };
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    // ------------------------------------------------------------------ mapping
    public static string Norm(string? s)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Has(string norm, params string[] phrases) =>
        phrases.Any(p => (" " + norm + " ").Contains(" " + p + " "));

    /// <summary>Returns the lead field a question most likely answers, or null.</summary>
    public static string? Detect(FormAnswer a)
    {
        var t = a.Type.ToUpperInvariant();
        var l = Norm(a.Label);
        if (t == "INPUT_EMAIL" || Has(l, "email", "e mail", "emailaddress")) return "Email";
        if (t == "INPUT_PHONE_NUMBER" || Has(l, "phone", "mobile", "cell", "cellphone", "whatsapp", "telephone", "tel", "contact number")) return "Phone";
        if (Has(l, "linkedin")) return "LinkedIn";
        if (Has(l, "first name", "firstname", "given name", "forename")) return "FirstName";
        if (Has(l, "surname", "last name", "lastname", "family name")) return "Surname";
        if (Has(l, "job title", "jobtitle", "position", "designation", "role", "title")) return "JobTitle";
        if (Has(l, "company", "organisation", "organization", "employer", "business name", "business")) return "Company";
        if (Has(l, "industry", "sector")) return "Industry";
        if (Has(l, "city", "town")) return "City";
        if (Has(l, "province", "state")) return "Province";
        if (l == "name" || Has(l, "full name", "fullname", "your name", "contact name", "name and surname", "name surname")) return "FullName";
        if (Has(l, "message", "comment", "comments", "note", "notes", "enquiry", "inquiry", "how can we help", "tell us", "question")) return "Message";
        return null;
    }

    /// <summary>Parses "LeadField = Question label" lines. Returns the error text if a line is invalid.</summary>
    public static (Dictionary<string, string> Map, string? Error) ParseMapping(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var i = line.IndexOf('=');
            if (i <= 0 || i == line.Length - 1) return (map, $"Invalid mapping line “{Trunc(line, 60)}”. Use: LeadField = Question label");
            var field = line[..i].Trim().Replace(" ", "");
            var match = FieldNames.FirstOrDefault(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (match is null) return (map, $"Unknown lead field “{line[..i].Trim()}”. Valid fields: {string.Join(", ", FieldNames)}.");
            map[Norm(line[(i + 1)..])] = match;
        }
        return (map, null);
    }

    public static MappedLead Map(IEnumerable<FormAnswer> answers, string? mappingText)
    {
        var result = new MappedLead();
        var (overrides, _) = ParseMapping(mappingText);
        var list = answers.ToList();
        var used = new HashSet<int>();

        // 1) explicit overrides win
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Value.Length == 0) continue;
            if (overrides.TryGetValue(Norm(list[i].Label), out var field) && !result.Fields.ContainsKey(field))
            { result.Fields[field] = list[i].Value; used.Add(i); }
        }
        // 2) auto-detect the rest (first match per field wins)
        for (var i = 0; i < list.Count; i++)
        {
            if (used.Contains(i) || list[i].Value.Length == 0) continue;
            var field = Detect(list[i]);
            if (field != null && !result.Fields.ContainsKey(field)) { result.Fields[field] = list[i].Value; used.Add(i); }
        }
        for (var i = 0; i < list.Count; i++)
            if (!used.Contains(i) && list[i].Value.Length > 0) result.Unmapped.Add($"{(list[i].Label.Length > 0 ? list[i].Label : list[i].Key)}: {list[i].Value}");

        Normalise(result);
        return result;
    }

    private static readonly System.ComponentModel.DataAnnotations.EmailAddressAttribute EmailCheck = new();

    private static void Normalise(MappedLead m)
    {
        // split a full name when separate first/last answers were not given
        if (m.Get("FirstName") is null && m.Get("FullName") is { } full)
        {
            var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) m.Fields["FirstName"] = parts[0];
            else { m.Fields["FirstName"] = string.Join(' ', parts[..^1]); m.Fields["Surname"] = parts[^1]; }
        }
        if (m.Fields.TryGetValue("Email", out var email))
        {
            email = email.Trim().ToLowerInvariant();
            if (email.Length <= 200 && EmailCheck.IsValid(email)) m.Fields["Email"] = email;
            else { m.Fields.Remove("Email"); m.Warnings.Add($"“{Trunc(email, 60)}” is not a valid email address."); }
        }
        if (m.Fields.TryGetValue("Phone", out var phone))
        {
            var cleaned = new string(phone.Where(c => char.IsDigit(c) || "+ ()-.".Contains(c)).ToArray()).Trim();
            if (cleaned.Count(char.IsDigit) >= 5) m.Fields["Phone"] = Trunc(cleaned, 40)!;
            else { m.Fields.Remove("Phone"); m.Warnings.Add("The phone number looked invalid and was ignored."); }
        }
        if (m.Fields.TryGetValue("LinkedIn", out var li))
        {
            var url = li.Contains("://") ? li : "https://" + li.TrimStart('/');
            if (UiHelpers.IsSafeHttpUrl(url) && url.Length <= 300) m.Fields["LinkedIn"] = url;
            else { m.Fields.Remove("LinkedIn"); m.Warnings.Add("The LinkedIn answer was not a valid link and was ignored."); }
        }
        foreach (var (field, max) in new[] { ("FirstName", 100), ("Surname", 100), ("JobTitle", 150), ("Company", 200), ("Industry", 150), ("City", 100), ("Province", 60) })
            if (m.Fields.TryGetValue(field, out var v) && v.Length > max) m.Fields[field] = v[..max];
    }

    /// <summary>Dry run used by the "test a sample payload" tool.</summary>
    public static (ParsedSubmission? Parsed, MappedLead? Mapped, string? Error) TryProcess(string json, string? mappingText)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var parsed = Parse(doc.RootElement);
            if (parsed.Answers.Count == 0) return (parsed, null, "No answers were found in this payload. Expected data.fields[] (Tally), fields[] or a flat JSON object.");
            return (parsed, Map(parsed.Answers, mappingText), null);
        }
        catch (JsonException ex) { return (null, null, "That is not valid JSON: " + ex.Message); }
    }
}
