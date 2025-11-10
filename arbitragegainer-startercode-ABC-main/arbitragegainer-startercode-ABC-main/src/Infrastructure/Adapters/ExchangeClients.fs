namespace Infrastructure.Adapters

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open Model

module ExchangeClients =
    let private bitfinexUrl = "https://api-pub.bitfinex.com/v2/conf/pub:list:pair:exchange"
    let private bitstampUrl = "https://www.bitstamp.net/api/v2/trading-pairs-info/"
    let private krakenUrl = "https://api.kraken.com/0/public/AssetPairs"

    let private toDomainError exchange message =
        ExternalDependencyError(exchange, message)

    let private httpGet (client: HttpClient) (exchange: Exchange) (url: string) =
        async {
            try
                use request = new HttpRequestMessage(HttpMethod.Get, url)
                use! response = client.SendAsync(request) |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Ok body
                else
                    return Error(toDomainError exchange $"Status code {int response.StatusCode}")
            with ex ->
                return Error(toDomainError exchange ex.Message)
        }

    let private parseBitfinexContent (content: string) =
        try
            use document = JsonDocument.Parse(content)

            document.RootElement.EnumerateArray()
            |> Seq.collect (fun inner -> inner.EnumerateArray())
            |> Seq.choose (fun element -> if element.ValueKind = JsonValueKind.String then element.GetString() |> Option.ofObj else None)
            |> PairNormalization.tryParseMany
        with ex ->
            Error(ValidationError $"Unable to parse Bitfinex payload: {ex.Message}")

    let private parseBitstampContent (content: string) =
        try
            use document = JsonDocument.Parse(content)

            document.RootElement.EnumerateArray()
            |> Seq.choose (fun element ->
                let mutable nameElement = Unchecked.defaultof<JsonElement>

                if element.TryGetProperty("name", &nameElement) then
                    nameElement.GetString() |> Option.ofObj
                else
                    None)
            |> PairNormalization.tryParseMany
            |> Result.mapError id
        with ex ->
            Error(ValidationError $"Unable to parse Bitstamp payload: {ex.Message}")

    let private parseKrakenContent (content: string) =
        try
            use document = JsonDocument.Parse(content)

            let mutable errorElement = Unchecked.defaultof<JsonElement>

            if document.RootElement.TryGetProperty("error", &errorElement) then
                let errors = errorElement

                if errors.ValueKind = JsonValueKind.Array && errors.GetArrayLength() > 0 then
                    let message = errors[0].GetString()
                    match message with
                    | null -> ()
                    | _ -> raise (Exception($"Kraken error: {message}"))

            let resultProperty = document.RootElement.GetProperty("result")

            resultProperty.EnumerateObject()
            |> Seq.choose (fun property ->
                let value = property.Value

                let mutable nameElement = Unchecked.defaultof<JsonElement>

                if value.TryGetProperty("wsname", &nameElement) then
                    let wsname = nameElement.GetString()

                    match wsname with
                    | null -> None
                    | name ->
                        if name.Contains "/" then
                            let parts = name.Split('/')

                            if parts.Length = 2 then
                                Some(parts[0], parts[1])
                            else
                                None
                        else
                            None
                else
                    None)
            |> Seq.map (fun (baseSymbol, quoteSymbol) -> CurrencyPair.create baseSymbol quoteSymbol)
            |> Seq.fold
                (fun state result ->
                    match state, result with
                    | Error err, _ -> Error err
                    | _, Error err -> Error err
                    | Ok pairs, Ok pair -> Ok(Set.add pair pairs))
                (Ok Set.empty)
        with
        | :? KeyNotFoundException as ex ->
            Error(ValidationError $"Unexpected Kraken payload shape: {ex.Message}")
        | ex ->
            Error(ValidationError $"Unable to parse Kraken payload: {ex.Message}")

    let private fetchPairs
        (client: HttpClient)
        (exchange: Exchange)
        (parseFn: string -> Result<Set<CurrencyPair>, DomainError>)
        (url: string)
        =
        async {
            let! response = httpGet client exchange url

            match response with
            | Error err -> return Error err
            | Ok content -> return parseFn content
        }

    let createBitfinexFetcher (client: HttpClient) () =
        fetchPairs client Bitfinex parseBitfinexContent bitfinexUrl

    let createBitstampFetcher (client: HttpClient) () =
        fetchPairs client Bitstamp parseBitstampContent bitstampUrl

    let createKrakenFetcher (client: HttpClient) () =
        fetchPairs client Kraken parseKrakenContent krakenUrl
