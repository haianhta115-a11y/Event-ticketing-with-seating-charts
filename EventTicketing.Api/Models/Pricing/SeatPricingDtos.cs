using System.ComponentModel.DataAnnotations;

namespace EventTicketing.Api.Models.Pricing;

public class SeatCategoryPricingItemDto
{
    [Required(ErrorMessage = "Mã hạng ghế là bắt buộc.")]
    public int CategoryId { get; set; }

    /// <summary>
    /// Alias to support either "categoryId" or "seatCategoryId" in incoming payload.
    /// </summary>
    public int SeatCategoryId
    {
        get => CategoryId;
        set => CategoryId = value;
    }

    [Range(0, double.MaxValue, ErrorMessage = "Giá vé phải lớn hơn hoặc bằng 0.")]
    public decimal Price { get; set; }
}

public class UpdateSeatPricingRequest
{
    [Required(ErrorMessage = "Danh sách giá vé không được để trống.")]
    public List<SeatCategoryPricingItemDto> Categories { get; set; } = new();

    /// <summary>
    /// Alias to support either "categories" or "prices" in incoming payload.
    /// </summary>
    public List<SeatCategoryPricingItemDto> Prices
    {
        get => Categories;
        set => Categories = value;
    }
}

public class SeatCategoryPricingResponseDto
{
    public int Id { get; set; }
    public int ShowtimeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int SeatCount { get; set; }
}
