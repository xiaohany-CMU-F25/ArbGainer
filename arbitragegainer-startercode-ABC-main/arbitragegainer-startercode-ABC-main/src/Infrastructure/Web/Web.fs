namespace Web

open System
open System.Text
open Application
open Application.CompositionRoot
open Application.MarketDataWorkflow
open Logging.Logger
open Model
open Core.Model
open Suave
open Suave.Filters
open Suave.Operators
open Suave.RequestErrors
open Suave.ServerErrors
open Suave.Successful
open Suave.Writers
open Newtonsoft.Json
open Newtonsoft.Json.Serialization

module WebApp =

    (* Default trading strategy — can be changed by user via PUT /strategy *)
    let mutable currentStrategy: TradingStrategy =
        { numCryptoPairs = 0
          minSpread = 0.01
          minTransactionProfit = 1.00
          maxTransactionValue = 1000.0
          maxTradingValue = 5000.0
          initialInvestment = 10000 }

    let private servicesLazy = lazy (CompositionRoot.createServices ())

    // ----------------------------
    // JSON helpers
    // ----------------------------
    let private jsonSettings =
        JsonSerializerSettings(
            ContractResolver = CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        )

    let private toJson (o: obj) = JsonConvert.SerializeObject(o, jsonSettings)

    let private writeJson (status: string -> WebPart) (payload: obj) : WebPart =
        let jsonString = toJson payload
        status jsonString
        >=> setHeader "Content-Type" "application/json; charset=utf-8"
        >=> setHeader "Cache-Control" "no-store"

    let private jsonOk o = writeJson OK o
    let private jsonAccepted o = writeJson ACCEPTED o
    let private jsonBadRequest msg = writeJson BAD_REQUEST {| error = msg |}
    let private jsonServiceUnavailable msg = writeJson SERVICE_UNAVAILABLE {| error = msg |}
    let private jsonInternal msg = writeJson INTERNAL_ERROR {| error = msg |}

    // ----------------------------
    // Domain error mapping
    // ----------------------------
    let private domainErrorToHttp err =
        match err with
        | ValidationError message -> jsonBadRequest message
        | ExternalDependencyError(exchange, message) ->
            jsonServiceUnavailable $"Exchange {exchange}: {message}"
        | RepositoryError message -> jsonInternal message

    // ----------------------------
    // Utility converters
    // ----------------------------
    let private iso (dt: DateTime) = dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

    let private tradingStateToString = function
        | Idle -> "Idle"
        | Running -> "Running"
        | Halted _ -> "Halted"

    let private haltReasonToString = function
        | ManualStop -> "ManualStop"
        | ThresholdReached -> "ThresholdReached"
        | FaultyOrder -> "FaultyOrder"

    let private exchangeToString = function
        | ExchangeId.Bitfinex -> "Bitfinex"
        | ExchangeId.Bitstamp -> "Bitstamp"
        | ExchangeId.Kraken -> "Kraken"
        | x -> string x

    // ----------------------------
    // DTOs
    // ----------------------------
    type ErrorInfoDto = { code: string; message: string; at: string }

    type TradingStatusDto =
        { state: string
          since: string
          reason: string option
          lastError: ErrorInfoDto option }

    type QuoteDto =
        { pair: string
          exchange: string
          bidPrice: decimal
          bidSize: decimal
          askPrice: decimal
          askSize: decimal
          receivedAt: string }

    type MarketDataDto =
        { timestamp: string
          data: QuoteDto list }

    // ----------------------------
    // Formatters
    // ----------------------------
    let private mapStatus (s: TradingStatus) : TradingStatusDto =
        { state = tradingStateToString s.State
          since = iso s.Since
          reason = s.Reason |> Option.map haltReasonToString
          lastError =
            s.LastError
            |> Option.map (fun e -> { code = e.Code; message = e.Message; at = iso e.At }) }

    let private mapMarketData () : MarketDataDto =
        let snap = getMarketDataSnapshot ()
        let rows =
            snap.Quotes
            |> Map.toList
            |> List.collect (fun (pair, exchangeMap) ->
                exchangeMap
                |> Map.toList
                |> List.map (fun (ex, cq) ->
                    { pair = pair
                      exchange = exchangeToString ex
                      bidPrice = cq.Quote.BidPrice
                      bidSize = cq.Quote.BidSize
                      askPrice = cq.Quote.AskPrice
                      askSize = cq.Quote.AskSize
                      receivedAt = iso cq.ReceivedAt }))
        { timestamp = iso snap.Timestamp; data = rows }

    // ----------------------------
    // Controllers
    // ----------------------------
    let private pairsPayload snapshotOpt =
        let pairs =
            snapshotOpt
            |> Option.map (fun snapshot -> snapshot.pairs |> List.map CurrencyPair.toStorageKey)
            |> Option.defaultValue []
        {| pairs = pairs |}

    let private postCrossTradedController (crossApi: CrossTradedPairsApi) =
        fun ctx ->
            async {
                let logger = createLogger ()
                logger $"{crossApi.IdentificationMetricName} - start"
                let! result = crossApi.Refresh logger
                return!
                    match result with
                    | Ok snapshot -> jsonOk (pairsPayload (Some snapshot)) ctx
                    | Error err -> domainErrorToHttp err ctx
            }

    let private getCrossTradedController (crossApi: CrossTradedPairsApi) =
        fun ctx ->
            async {
                let logger = createLogger ()
                logger $"{crossApi.RetrievalMetricName} - start"
                let! result = crossApi.GetLatest logger
                return!
                    match result with
                    | Ok snapshot -> jsonOk (pairsPayload snapshot) ctx
                    | Error err -> domainErrorToHttp err ctx
            }

    let checkhealth = jsonOk {| status = "ok" |}

    let getStrategyController = fun ctx -> jsonOk currentStrategy ctx

    let putStrategyController =
        request (fun r ->
            let body = Encoding.UTF8.GetString r.rawForm
            let parsed = System.Text.Json.JsonSerializer.Deserialize<TradingStrategy>(body)
            currentStrategy <- parsed
            jsonOk parsed)

    // ----------------------------
    // Trading controllers
    // ----------------------------
    let private startTradingHandler : WebPart =
        fun ctx -> async {
            let! result = startTrading ()
            match result with
            | Ok status ->
                let! response = jsonAccepted {| state = tradingStateToString status.State |} ctx
                return response
            | Error err ->
                let! response = jsonBadRequest err ctx
                return response
        }

    let private stopTradingHandler : WebPart =
        fun ctx -> async {
            let! result = stopTrading ()
            match result with
            | Ok status ->
                let! response = jsonAccepted {| state = tradingStateToString status.State |} ctx
                return response
            | Error err ->
                let! response = jsonBadRequest err ctx
                return response
        }

    let private statusHandler : WebPart =
        fun ctx -> async {
            let status = getStatus () |> mapStatus
            let! response = jsonOk status ctx
            return response
        }

    let private marketDataHandler : WebPart =
        fun ctx -> async {
            let! result =
                async {
                    return Ok (mapMarketData ())
                }
                |> Async.Catch
            
            let finalResult =
                result
                |> function
                    | Choice1Of2 r -> r
                    | Choice2Of2 ex ->
                        printfn "[ERROR] Market data handler failed: %s" ex.Message
                        printfn "[ERROR] Stack trace: %s" ex.StackTrace
                        Error $"Failed to retrieve market data: {ex.Message}"
            
            return!
                match finalResult with
                | Ok dto ->
                    jsonOk dto ctx
                | Error msg ->
                    jsonInternal msg ctx
        }

    // ----------------------------
    // Route composition
    // ----------------------------
    let private createApp services =
        let crossApi = services.CrossTradedPairs

        choose [
            GET >=> choose [
                path "/health" >=> checkhealth
                path "/strategy" >=> getStrategyController
                path "/pairs/cross-traded" >=> getCrossTradedController crossApi
                path "/trading/status" >=> statusHandler
                path "/market-data" >=> marketDataHandler
            ]
            PUT >=> choose [
                path "/strategy" >=> putStrategyController
            ]
            POST >=> choose [
                path "/pairs/cross-traded" >=> postCrossTradedController crossApi
                path "/trading/start" >=> startTradingHandler
                path "/trading/stop"  >=> stopTradingHandler
            ]
        ]

    let private app () = servicesLazy.Value |> createApp

    // ----------------------------
    // Console & Web entry points
    // ----------------------------
    let runConsole () =
        printfn "Running in console mode..."
        0

    let runWeb () =
        let port =
            match Environment.GetEnvironmentVariable("PORT") with
            | null | "" -> 8080
            | v when Int32.TryParse(v) |> fst -> Int32.Parse v
            | _ -> 8080

        let binding = HttpBinding.createSimple HTTP "0.0.0.0" port
        let config = { defaultConfig with bindings = [ binding ] }

        printfn "Suave API running on http://0.0.0.0:%d" port
        printfn "Try: curl http://localhost:%d/health" port

        startWebServer config (app ())
        0
