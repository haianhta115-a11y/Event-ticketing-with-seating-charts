using System.Text.Json;
using System.Text.Json.Nodes;
using EventTicketing.Api.Models.SeatingChart;
using EventTicketing.Api.Services;

namespace EventTicketing.Api.SeatMaps;

public sealed record PreparedSeatingChart(SeatingChartValidationResult Validation, SeatMapImportDocument? Document);

// Shared read-only preparation for validate, preview and persistence. Never opens a database transaction.
public sealed class SeatingChartPreparation(ISeatingChartValidator validator)
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public async Task<PreparedSeatingChart> ReadFileAsync(IFormFile? file, CancellationToken ct = default)
    {
        if (file == null) return Failure("file", "FILE_NOT_FOUND", "Không tìm thấy tệp sơ đồ.");
        if (file.Length > MaximumBytes) return Failure("file", "FILE_TOO_LARGE", "Tệp sơ đồ tối đa 5 MB.");
        if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Failure("file.extension", "INVALID_FILE_EXTENSION", "Chỉ chấp nhận tệp .json.");
        await using var stream = file.OpenReadStream();
        return await ReadAsync(stream, ct);
    }
    public async Task<PreparedSeatingChart> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) return Failure("file", "FILE_TOO_LARGE", "Tệp sơ đồ tối đa 5 MB.");
            await buffer.WriteAsync(block.AsMemory(0, count), ct);
        }
        if (buffer.Length == 0) return Failure("file", "EMPTY_FILE", "Tệp sơ đồ rỗng.");
        buffer.Position = 0;
        try
        {
            using var json = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }, ct);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Failure("root", "INVALID_ROOT_OBJECT", "Gốc tệp phải là một đối tượng JSON.");
            if (CountSeats(json.RootElement, typeof(SeatingChartFileDto)) > 20000)
                return Failure("seats", "TOO_MANY_SEATS", "Sơ đồ tối đa 20000 ghế theo giới hạn import hiện tại.");
            var typeErrors = new List<SeatingChartValidationError>();
            var normalized = CheckTypes(json.RootElement, typeof(SeatingChartFileDto), "", typeErrors)!;
            var dto = normalized.Deserialize<SeatingChartFileDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var prepared = Prepare(dto);
            if (typeErrors.Count == 0) return prepared;
            return new(SeatingChartValidationResult.Failure(typeErrors.Concat(prepared.Validation.Errors).ToList()), null);
        }
        catch (JsonException error)
        {
            var line = (error.LineNumber ?? 0) + 1;
            var bytes = buffer.ToArray(); var start = 0;
            for (var i = 0L; i < line - 1; i++)
            {
                var next = Array.IndexOf(bytes, (byte)'\n', start);
                if (next < 0) break;
                start = next + 1;
            }
            var prefix = (int)Math.Min(error.BytePositionInLine ?? 0, bytes.Length - start);
            var column = System.Text.Encoding.UTF8.GetString(bytes, start, prefix).EnumerateRunes().Count() + 1;
            return new(SeatingChartValidationResult.SingleError($"Dòng {line}, cột {column}", error.Path ?? "root",
                "INVALID_JSON_SYNTAX", $"JSON không hợp lệ tại dòng {line}, cột {column}."), null);
        }
    }

    private static int CountSeats(JsonElement value, Type type)
    {
        if (value.ValueKind != JsonValueKind.Object) return 0;
        var count = 0;
        var properties = type.GetProperties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var field in value.EnumerateObject())
        {
            if (!properties.TryGetValue(field.Name, out var property) || field.Value.ValueKind != JsonValueKind.Array ||
                !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>)) continue;
            var itemType = property.PropertyType.GenericTypeArguments[0];
            if (itemType == typeof(SeatDto)) count += field.Value.GetArrayLength();
            else foreach (var item in field.Value.EnumerateArray())
            {
                count += CountSeats(item, itemType);
                if (count > 20000) return count;
            }
            if (count > 20000) return count;
        }
        return count;
    }

    // Collect all schema type errors, substituting null only in a temporary DTO so structural
    // validation can also report missing fields in the same pass. Invalid input never reaches import.
    private static JsonNode? CheckTypes(JsonElement value, Type type, string path, List<SeatingChartValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        bool valid;
        if (type == typeof(string)) valid = value.ValueKind == JsonValueKind.String;
        else if (type == typeof(int)) valid = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
        else if (type == typeof(decimal)) valid = value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _);
        else if (type == typeof(double)) valid = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
        else if (type == typeof(bool)) valid = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        else if (type == typeof(DateTime)) valid = value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out _);
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            valid = value.ValueKind == JsonValueKind.Array;
            if (valid)
            {
                var array = new JsonArray(); var index = 0;
                foreach (var child in value.EnumerateArray()) array.Add(CheckTypes(child, type.GenericTypeArguments[0], $"{path}[{index++}]", errors));
                return array;
            }
        }
        else
        {
            valid = value.ValueKind == JsonValueKind.Object;
            if (valid)
            {
                var obj = new JsonObject();
                var properties = type.GetProperties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in value.EnumerateObject())
                {
                    if (!properties.TryGetValue(child.Name, out var property)) continue; // Existing contract ignores unknown fields.
                    var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                    var childPath = path.Length == 0 ? name : $"{path}.{name}";
                    if (!seen.Add(name)) { errors.Add(new(childPath, childPath, "DUPLICATE_FIELD", "Trường JSON xuất hiện nhiều lần.")); continue; }
                    obj[name] = CheckTypes(child.Value, property.PropertyType, childPath, errors);
                }
                return obj;
            }
        }
        if (valid) return JsonNode.Parse(value.GetRawText());
        errors.Add(new(path, path, "INVALID_FIELD_TYPE", "Trường có kiểu dữ liệu hoặc giá trị không phù hợp schema."));
        return null;
    }

    public PreparedSeatingChart Prepare(SeatingChartFileDto dto)
    {
        // Legacy pure validator keeps its price contract for existing callers/tests. The application
        // import workflow has no price requirement until S-15, and every route uses this same rule.
        var errors = validator.ValidateDto(dto).Errors.Where(e => e.ErrorCode != "MISSING_PRICE").ToList();
        var reportedFields = errors.Select(e => e.Path).ToHashSet();
        var duplicateFields = errors.Where(e => e.ErrorCode == "DUPLICATE_SEAT").Select(e => e.Path).ToHashSet();
        var coordinateFields = errors.Where(e => e.ErrorCode == "INVALID_COORDINATES").Select(e => e.Path).ToHashSet();
        var identities = new Dictionary<(string Row, string Number), string>();
        var count = 0;
        void Check(SeatDto? seat, string path, string? parentRow = null, string? parentCategory = null)
        {
            count++;
            if (seat == null) return; // Existing validator reports NULL_SEAT with its exact path.
            var row = (seat.Row ?? parentRow)?.Trim() ?? "";
            var number = seat.SeatNumber?.Trim() ?? "";
            var category = (seat.Category ?? parentCategory ?? "Thường").Trim();
            void Error(string field, string code, string message)
            {
                reportedFields.Add($"{path}.{field}");
                errors.Add(new($"Ghế {row}-{number}", $"{path}.{field}", code, message));
            }
            void Length(string value, int max, string field)
            {
                if (value.Length == 0 && !reportedFields.Contains($"{path}.{field}")) Error(field, "REQUIRED_FIELD", $"{field} là bắt buộc.");
                if (value.Length > max) Error(field, "FIELD_TOO_LONG", $"{field} tối đa {max} ký tự.");
            }
            Length(row, 50, "row"); Length(number, 50, "seatNumber"); Length(category, 100, "category");
            if (parentRow != null && seat.Row != null && !parentRow.Trim().Equals(row, StringComparison.OrdinalIgnoreCase))
                Error("row", "ROW_MISMATCH", "Hàng của ghế không khớp hàng chứa ghế.");
            if (row.Length > 0 && number.Length > 0)
            {
                var key = (row.ToUpperInvariant(), number.ToUpperInvariant());
                if (identities.TryGetValue(key, out var first) && !duplicateFields.Contains($"{path}.seatNumber"))
                    Error("seatNumber", "DUPLICATE_SEAT", $"Ghế {row}-{number} trùng với {first}; hàng/số ghế phải unique trong suất diễn.");
                else identities.TryAdd(key, path);
            }
            if (!string.IsNullOrWhiteSpace(seat.Status) && !seat.Status.Trim().Equals("Available", StringComparison.OrdinalIgnoreCase))
                Error("status", "UNSUPPORTED_IMPORT_STATUS", "Tệp sơ đồ chỉ được tạo ghế Available, không cấp booking status.");
            if (seat.HeldUntilUtc != null) Error("heldUntilUtc", "FORBIDDEN_HOLD_DATA", "Tệp sơ đồ không được cấp thời hạn giữ chỗ.");
            foreach (var (value, field) in new[] { (seat.X, "x"), (seat.Y, "y") })
                if (value.HasValue && (!double.IsFinite(value.Value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value.Value)))
                    if (!coordinateFields.Contains($"{path}.{field}"))
                        Error(field, "INVALID_COORDINATES", "Tọa độ phải là số nguyên không âm trong giới hạn của schema.");
        }
        for (var i = 0; i < dto.Seats?.Count; i++) Check(dto.Seats[i], $"seats[{i}]");
        for (var i = 0; i < dto.Sections?.Count; i++)
        {
            var section = dto.Sections[i]; if (section == null) continue;
            for (var j = 0; j < section.Seats?.Count; j++) Check(section.Seats[j], $"sections[{i}].seats[{j}]", parentCategory: section.Category);
            for (var j = 0; j < section.Rows?.Count; j++)
            {
                var row = section.Rows[j]; if (row == null) continue;
                for (var k = 0; k < row.Seats?.Count; k++) Check(row.Seats[k], $"sections[{i}].rows[{j}].seats[{k}]", row.RowNumber, section.Category);
            }
        }
        if (count > 20000) errors.Add(new("Sơ đồ", "seats", "TOO_MANY_SEATS", "Sơ đồ tối đa 20000 ghế theo giới hạn import hiện tại."));
        if (errors.Count > 0) return new(SeatingChartValidationResult.Failure(errors), null);
        var document = SeatMapDocumentAdapter.Convert(dto);
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string Canonical(string name, Dictionary<string, string> names)
        {
            var trimmed = name.Trim();
            if (!names.TryGetValue(trimmed, out var canonical)) names[trimmed] = canonical = trimmed;
            return canonical;
        }
        var normalized = document.Seats!.Select(s => new SeatMapImportItem
        {
            Row = Canonical(s!.Row!, rows),
            Number = s.Number!.Trim(),
            Category = Canonical(s.Category!, categories),
            X = s.X,
            Y = s.Y
        }).ToList();
        document = new SeatMapImportDocument { Seats = normalized.Select(s => (SeatMapImportItem?)s).ToList() };
        var summary = SeatingChartValidationResult.Success(count,
            0, normalized.Select(s => s.Row!.ToUpperInvariant()).Distinct().Count());
        // Structural validator returns no summary when the only legacy errors were missing prices.
        summary.TotalSections = (dto.Sections?.Where(s => s != null).Select(s => s.Name) ?? [])
            .Concat(dto.Seats?.Where(s => s != null).Select(s => s.Section) ?? []).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().ToUpperInvariant()).Distinct().Count();
        return new(summary, document);
    }
    public static PreparedSeatingChart Failure(string path, string code, string message) =>
        new(SeatingChartValidationResult.SingleError("Tệp sơ đồ", path, code, message), null);
}
