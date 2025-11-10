namespace Model

open System

[<StructuralEquality; StructuralComparison>]
type CurrencySymbol = private CurrencySymbol of string

module CurrencySymbol =
    let value (CurrencySymbol symbol) = symbol

    let create (symbol: string) =
        if String.IsNullOrWhiteSpace symbol then
            Error(ValidationError "Currency symbol cannot be empty.")
        else
            symbol.Trim().ToUpperInvariant() |> CurrencySymbol |> Ok

[<StructuralEquality; StructuralComparison>]
type CurrencyPair =
    private
        | CurrencyPair of CurrencySymbol * CurrencySymbol

module CurrencyPair =
    let create baseSymbol quoteSymbol =
        CurrencySymbol.create baseSymbol
        |> Result.bind (fun baseVal ->
            CurrencySymbol.create quoteSymbol
            |> Result.bind (fun quoteVal -> CurrencyPair(baseVal, quoteVal) |> Ok))

    let baseCurrency (CurrencyPair(baseSymbol, _)) = baseSymbol

    let quoteCurrency (CurrencyPair(_, quoteSymbol)) = quoteSymbol

    let toStorageKey pair =
        let baseValue = pair |> baseCurrency |> CurrencySymbol.value
        let quoteValue = pair |> quoteCurrency |> CurrencySymbol.value
        $"{baseValue}-{quoteValue}"

    let tryFromStorageKey (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error(ValidationError "Currency pair value cannot be empty.")
        else
            let parts = value.Trim().Split('-')

            if parts.Length <> 2 then
                Error(ValidationError $"Value '{value}' is not a valid currency pair format.")
            else
                create parts[0] parts[1]
