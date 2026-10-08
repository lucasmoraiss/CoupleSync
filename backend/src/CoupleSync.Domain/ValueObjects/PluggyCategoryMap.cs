namespace CoupleSync.Domain.ValueObjects;

/// <summary>
/// The category Pluggy gives a transaction → one of the seven keys of <see cref="TransactionCategories"/>.
/// Pluggy identifies a category by an 8-digit code in which each pair of digits is a level of its tree
/// ("19000000" Transportation → "19010000" Taxi and ride-hailing): the longest prefix listed here decides, so a
/// sub-category Pluggy adds later follows its parent. Without a known code the name decides (in English, as the API
/// sends it, or in Portuguese, as Pluggy translates it). The table is locked by a test: changing it is a decision.
/// </summary>
public static class PluggyCategoryMap
{
    /// <summary>Code prefix (2, 4 or 6 digits) → key of the app.</summary>
    public static IReadOnlyDictionary<string, string> ByIdPrefix { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["10"] = "ALIMENTACAO",   // Groceries
        ["11"] = "ALIMENTACAO",   // Food and drinks (eating out, delivery)
        ["19"] = "TRANSPORTE",    // Transportation (taxi, public transport, fuel, parking, tolls...)
        ["2004"] = "TRANSPORTE",  // Vehicle insurance
        ["18"] = "SAUDE",         // Healthcare (dentist, pharmacy, hospitals...)
        ["0703"] = "SAUDE",       // Wellness and fitness
        ["2003"] = "SAUDE",       // Health insurance
        ["17"] = "MORADIA",       // Housing (rent, utilities, houseware, property tax)
        ["0701"] = "MORADIA",     // Telecommunications (internet, mobile, TV)
        ["2002"] = "MORADIA",     // Home insurance
        ["08"] = "COMPRAS",       // Shopping
        ["21"] = "LAZER",         // Leisure
        ["09"] = "LAZER",         // Digital services (games, video and music streaming)
        ["0704"] = "LAZER",       // Tickets (stadiums, museums, cinema, concerts)
        ["12"] = "LAZER",         // Travel
    };

    /// <summary>Category name, folded as <see cref="TransactionCategories.Fold"/> does → key of the app.</summary>
    public static IReadOnlyDictionary<string, string> ByName { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ALIMENTACAO"] = "ALIMENTACAO",
        ["ALIMENTOS E BEBIDAS"] = "ALIMENTACAO",
        ["FOOD AND DRINKS"] = "ALIMENTACAO",
        ["EATING OUT"] = "ALIMENTACAO",
        ["FOOD DELIVERY"] = "ALIMENTACAO",
        ["GROCERIES"] = "ALIMENTACAO",
        ["SUPERMERCADO"] = "ALIMENTACAO",
        ["MERCADO"] = "ALIMENTACAO",
        ["RESTAURANTES"] = "ALIMENTACAO",
        ["TRANSPORTE"] = "TRANSPORTE",
        ["TRANSPORTATION"] = "TRANSPORTE",
        ["TAXI AND RIDE-HAILING"] = "TRANSPORTE",
        ["PUBLIC TRANSPORTATION"] = "TRANSPORTE",
        ["GAS STATIONS"] = "TRANSPORTE",
        ["AUTOMOTIVE"] = "TRANSPORTE",
        ["SAUDE"] = "SAUDE",
        ["HEALTHCARE"] = "SAUDE",
        ["HEALTH"] = "SAUDE",
        ["PHARMACY"] = "SAUDE",
        ["FARMACIA"] = "SAUDE",
        ["MORADIA"] = "MORADIA",
        ["HOUSING"] = "MORADIA",
        ["RENT"] = "MORADIA",
        ["ALUGUEL"] = "MORADIA",
        ["UTILITIES"] = "MORADIA",
        ["COMPRAS"] = "COMPRAS",
        ["SHOPPING"] = "COMPRAS",
        ["ONLINE SHOPPING"] = "COMPRAS",
        ["LAZER"] = "LAZER",
        ["LEISURE"] = "LAZER",
        ["ENTERTAINMENT"] = "LAZER",
        ["TRAVEL"] = "LAZER",
        ["VIAGEM"] = "LAZER",
        ["DIGITAL SERVICES"] = "LAZER",
    };

    /// <summary>The key of the app for a Pluggy category, or null when neither the code nor the name is known.</summary>
    public static string? TryMap(string? categoryId, string? categoryName)
    {
        var id = categoryId?.Trim();
        if (!string.IsNullOrEmpty(id))
        {
            foreach (var length in (int[])[6, 4, 2])
            {
                if (id.Length >= length && ByIdPrefix.TryGetValue(id[..length], out var byId)) return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(categoryName)) return null;
        return ByName.TryGetValue(TransactionCategories.Fold(categoryName), out var byName) ? byName : null;
    }

    /// <summary>The key of the app for a Pluggy category; anything unknown is OUTROS.</summary>
    public static string Map(string? categoryId, string? categoryName)
        => TryMap(categoryId, categoryName) ?? TransactionCategories.Other;
}
