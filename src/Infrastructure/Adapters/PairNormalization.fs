namespace Infrastructure.Adapters

open System
open Model

module PairNormalization =
    let private separators: char array = [| '-'; '_'; '/'; ':' |]

    let private knownQuoteSuffixes: string list =
        [ "USDT"
          "USDC"
          "BUSD"
          "DAI"
          "USD"
          "EUR"
          "GBP"
          "BTC"
          "ETH"
          "BNB"
          "SOL"
          "JPY"
          "AUD"
          "CAD"
          "CHF"
          "TRY"
          "MXN"
          "BRL"
          "ZAR" ]

    let private normalizeForSuffixParsing (value: string) =
        let upper = value.Trim().ToUpperInvariant()

        let withoutPrefix =
            if upper.StartsWith("T") && upper.Length > 3 then
                upper.Substring(1)
            else
                upper

        let withoutDerivatives =
            if withoutPrefix.EndsWith("F0") && withoutPrefix.Length > 2 then
                withoutPrefix.Substring(0, withoutPrefix.Length - 2)
            else
                withoutPrefix

        separators
        |> Array.fold (fun (acc: string) (sep: char) -> acc.Replace(sep.ToString(), "")) withoutDerivatives

    let private trySplitBySeparators (value: string) =
        let attempt (separator: char) =
            let parts = value.Split(separator)

            if parts.Length = 2 then
                Some(parts[0].Trim(), parts[1].Trim())
            else
                None

        separators |> Array.tryPick attempt

    let private trySplitBySuffixes (value: string) =
        let normalized = normalizeForSuffixParsing value

        knownQuoteSuffixes
        |> List.sortByDescending (fun suffix -> suffix.Length)
        |> List.tryPick (fun suffix ->
            if normalized.EndsWith suffix then
                let baseLength = normalized.Length - suffix.Length

                if baseLength > 0 then
                    Some(normalized.Substring(0, baseLength), suffix)
                else
                    None
            else
                None)

    let private isThreeLetterCode (symbol: string) =
        symbol.Length = 3 && symbol |> Seq.forall Char.IsLetter

    let tryParseSymbolPair (value: string) =
        let cleaned = value.Trim().ToUpperInvariant()

        let baseQuote =
            match trySplitBySeparators cleaned with
            | Some split -> Some split
            | None -> trySplitBySuffixes cleaned

        match baseQuote with
        | Some(baseSymbol, quoteSymbol) ->
            if isThreeLetterCode baseSymbol && isThreeLetterCode quoteSymbol then
                CurrencyPair.create baseSymbol quoteSymbol
            else
                Error(ValidationError $"Pair '{value}' discarded because symbols must be exactly 3 letters.")
        | None -> Error(ValidationError $"Unable to parse trading pair '{value}'.")

    let tryParseMany symbols =
        let folder (pairs, errors) symbol =
            match tryParseSymbolPair symbol with
            | Ok pair -> (pair :: pairs, errors)
            | Error err -> (pairs, err :: errors)

        let parsedPairs, errors = symbols |> Seq.fold folder ([], [])

        match parsedPairs with
        | [] ->
            match errors with
            | err :: _ -> Error err
            | [] -> Error(ValidationError "No valid cross-traded pairs were discovered.")
        | pairs ->
            pairs |> Set.ofList |> Ok
