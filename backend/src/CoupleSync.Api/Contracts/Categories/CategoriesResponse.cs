namespace CoupleSync.Api.Contracts.Categories;

public sealed record CategoryResponse(string Key, string Label);

public sealed record CategoriesResponse(IReadOnlyList<CategoryResponse> Categories);
