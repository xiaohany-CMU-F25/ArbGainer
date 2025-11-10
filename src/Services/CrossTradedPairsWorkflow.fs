namespace Services

open System
open Model

module CrossTradedPairsWorkflow =
    [<Literal>]
    let IdentificationMetricName = "Cross-Traded Currencies Identification"

    [<Literal>]
    let RetrievalMetricName = "Cross-Traded Currencies Retrieval"

    type ExchangePairFetcher = Exchange * (unit -> Async<Result<Set<CurrencyPair>, DomainError>>)

    type CrossTradedPairsRepository =
        {
            SaveSnapshot: CrossTradedPairsSnapshot -> Async<Result<unit, DomainError>>
            GetLatestSnapshot: unit -> Async<Result<CrossTradedPairsSnapshot option, DomainError>>
        }

    type Dependencies =
        {
            Providers: ExchangePairFetcher list
            Repository: CrossTradedPairsRepository
        }

    let private sequenceResults results =
        let folder state next =
            match state, next with
            | Error err, _ -> Error err
            | _, Error err -> Error err
            | Ok acc, Ok value -> Ok(value :: acc)

        results |> List.fold folder (Ok []) |> Result.map List.rev

    let private pairsOnAtLeastTwoExchanges (exchangePairs: (Exchange * Set<CurrencyPair>) list) =
        exchangePairs
        |> List.collect (fun (exchange, pairs) -> pairs |> Set.toList |> List.map (fun pair -> pair, exchange))
        |> List.fold
            (fun acc (pair, exchange) ->
                let updated =
                    match Map.tryFind pair acc with
                    | None -> Set.singleton exchange
                    | Some exchanges -> Set.add exchange exchanges

                Map.add pair updated acc)
            Map.empty
        |> Map.toList
        |> List.choose (fun (pair, exchanges) -> if Set.count exchanges >= 2 then Some pair else None)

    let refresh deps logMeasurement =
        async {
            if List.isEmpty deps.Providers then
                logMeasurement $"{IdentificationMetricName} - end (failed)"
                return Error(ValidationError "No exchange providers configured.")
            else
                let! fetched =
                    deps.Providers
                    |> List.map (fun (exchange, fetch) ->
                        async {
                            let! result = fetch ()
                            return result |> Result.map (fun pairs -> exchange, pairs)
                        })
                    |> Async.Parallel

                match fetched |> Array.toList |> sequenceResults with
                | Error err ->
                    logMeasurement $"{IdentificationMetricName} - end (failed)"
                    return Error err
                | Ok values ->
                    let filteredPairs =
                        values |> pairsOnAtLeastTwoExchanges |> List.sortBy CurrencyPair.toStorageKey

                    let snapshot: CrossTradedPairsSnapshot =
                        {
                            computedAt = DateTime.UtcNow
                            pairs = filteredPairs
                        }

                    logMeasurement $"{IdentificationMetricName} - end"

                    let! saveResult = deps.Repository.SaveSnapshot snapshot
                    return saveResult |> Result.map (fun _ -> snapshot)
        }

    let getLatest deps logMeasurement =
        async {
            let! snapshotResult = deps.Repository.GetLatestSnapshot()

            match snapshotResult with
            | Ok snapshot ->
                logMeasurement $"{RetrievalMetricName} - end"
                return Ok snapshot
            | Error err ->
                logMeasurement $"{RetrievalMetricName} - end (failed)"
                return Error err
        }
