module CrossTradedPairsWorkflowTests

open System
open Expecto
open Model
open Services.CrossTradedPairsWorkflow

let private pair baseSymbol quoteSymbol =
    match CurrencyPair.create baseSymbol quoteSymbol with
    | Ok value -> value
    | Error err -> failwithf "Failed to create pair: %A" err

let private okFetcher exchange pairs =
    (exchange, fun () -> async { return Ok(Set.ofList pairs) })

let private errorFetcher exchange message =
    (exchange, fun () -> async { return Error(ExternalDependencyError(exchange, message)) })

let private repoWithState () =
    let state = ref None

    let repo =
        {
            SaveSnapshot =
                fun snapshot ->
                    async {
                        state := Some snapshot
                        return Ok()
                    }
            GetLatestSnapshot = fun () -> async { return Ok(!state) }
        }

    repo, state

let tests =
    testList
        "Cross-traded pairs workflow"
        [
            testCaseAsync
                "intersects providers and persists snapshot"
                (async {
                    let repo, state = repoWithState ()

                    let deps: Dependencies =
                        {
                            Providers =
                                [
                                    okFetcher
                                        Exchange.Bitfinex
                                        [
                                            pair "BTC" "USD"
                                            pair "ETH" "USD"
                                        ]
                                    okFetcher
                                        Exchange.Bitstamp
                                        [
                                            pair "ETH" "USD"
                                            pair "SOL" "USD"
                                        ]
                                    okFetcher
                                        Exchange.Kraken
                                        [
                                            pair "SOL" "USD"
                                            pair "XRP" "USD"
                                        ]
                                ]
                            Repository = repo
                        }

                    let! result = refresh deps ignore

                    match result with
                    | Error err -> failtestf "Unexpected error %A" err
                    | Ok snapshot ->
                        Expect.equal
                            (snapshot.pairs |> List.map CurrencyPair.toStorageKey)
                            [
                                "ETH-USD"
                                "SOL-USD"
                            ]
                            "Pairs should include any symbol present on at least two exchanges"

                        match !state with
                        | None -> failtest "Snapshot was not persisted"
                        | Some stored ->
                            Expect.equal stored.pairs snapshot.pairs "Persisted snapshot should match result"
                })

            testCaseAsync
                "propagates upstream errors without persisting"
                (async {
                    let repo, state = repoWithState ()

                    let deps: Dependencies =
                        {
                            Providers =
                                [
                                    okFetcher Exchange.Bitfinex [ pair "BTC" "USD" ]
                                    errorFetcher Exchange.Bitstamp "timeout"
                                ]
                            Repository = repo
                        }

                    let! result = refresh deps ignore

                    match result with
                    | Ok _ -> failtest "Expected failure from provider"
                    | Error(ExternalDependencyError(exchange, _)) ->
                        Expect.equal exchange Exchange.Bitstamp "Should surface originating exchange"
                        Expect.equal !state None "Repository should not persist on error"
                    | Error other -> failtestf "Unexpected error %A" other
                })

            testCaseAsync
                "getLatest returns repository snapshot"
                (async {
                    let sampleSnapshot =
                        {
                            computedAt = DateTime.UtcNow
                            pairs = [ pair "SOL" "USD" ]
                        }

                    let deps: Dependencies =
                        {
                            Providers = []
                            Repository =
                                {
                                    SaveSnapshot = fun _ -> async { return Ok() }
                                    GetLatestSnapshot = fun () -> async { return Ok(Some sampleSnapshot) }
                                }
                        }

                    let! result = getLatest deps ignore

                    Expect.equal result (Ok(Some sampleSnapshot)) "Should return latest snapshot"
                })
        ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
