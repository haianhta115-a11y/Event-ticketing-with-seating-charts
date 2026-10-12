using EventTicketing.Api.Models.SeatingChart;

namespace EventTicketing.Api.SeatMaps;

// Flatten the existing public JSON contract without mutating the deserialized DTO.
public static class SeatMapDocumentAdapter
{
    public static SeatMapImportDocument Convert(SeatingChartFileDto dto)
    {
        var items = new List<SeatMapImportItem?>();
        void Add(SeatDto? seat, string? row = null, string? category = null)
        {
            if (seat == null) { items.Add(null); return; }
            if (!string.IsNullOrWhiteSpace(seat.Status) && !seat.Status.Trim().Equals("Available", StringComparison.OrdinalIgnoreCase))
                throw new SeatMapValidationException(["Sơ đồ không được cấp trạng thái giữ chỗ/bán vé hoặc trạng thái không được model hỗ trợ."]);
            if (row != null && seat.Row != null && !row.Trim().Equals(seat.Row.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new SeatMapValidationException(["Hàng của ghế không khớp với hàng chứa ghế trong tệp."]);
            if (seat.HeldUntilUtc != null) throw new SeatMapValidationException(["Tệp sơ đồ không được cấp thời hạn giữ chỗ."]);
            if (seat.X.HasValue != seat.Y.HasValue)
                throw new SeatMapValidationException(["Tọa độ X/Y phải được cung cấp cùng nhau hoặc cùng để trống."]);
            int? Coordinate(double? value)
            {
                if (value == null) return null;
                if (!double.IsFinite(value.Value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value.Value))
                    throw new SeatMapValidationException(["Tọa độ ghế phải là số nguyên không âm trong giới hạn của schema."]);
                return (int)value.Value;
            }
            items.Add(new SeatMapImportItem
            {
                Row = seat.Row ?? row,
                Number = seat.SeatNumber,
                Category = seat.Category ?? category ?? "Thường",
                X = Coordinate(seat.X),
                Y = Coordinate(seat.Y)
            });
        }
        foreach (var seat in dto.Seats ?? []) Add(seat);
        foreach (var section in dto.Sections ?? [])
        {
            if (section == null) throw new SeatMapValidationException(["Khu vực không được null."]);
            foreach (var seat in section.Seats ?? []) Add(seat, category: section.Category);
            foreach (var row in section.Rows ?? [])
            {
                if (row == null) throw new SeatMapValidationException(["Hàng ghế không được null."]);
                foreach (var seat in row.Seats ?? []) Add(seat, row.RowNumber, section.Category);
            }
        }
        return new SeatMapImportDocument { Seats = items };
    }
}
