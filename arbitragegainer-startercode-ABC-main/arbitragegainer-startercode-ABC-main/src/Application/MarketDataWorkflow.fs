namespace Application

open System
open System.Threading
open Core.Model
open Infrastructure.Adapters
open Logging.Logger
open System.Net.WebSockets

module MarketDataWorkflow =

    // State type
    type WorkflowState = {
        Status: TradingStatus
        Cache: Map<string, Map<ExchangeId, CachedQuote>>
        CancellationToken: CancellationTokenSource option
        Socket: ClientWebSocket option
    }

    // Initial state
    let private initialState = {
        Status = { State = Idle; Since = DateTime.UtcNow; Reason = None; LastError = None }
        Cache = Map.empty
        CancellationToken = None
        Socket = None
    }

    let private currentState = ref initialState

    let private logger = createLogger ()
    let private mongoConn = 
        Environment.GetEnvironmentVariable("MONGO_URI") 
        |> Option.ofObj 
        |> Option.defaultValue "mongodb://localhost:27017"

    // Pure function to update cache
    let private addQuoteToCache (quote: MarketQuote) (cache: Map<string, Map<ExchangeId, CachedQuote>>) : Map<string, Map<ExchangeId, CachedQuote>> =
        let now = DateTime.UtcNow
        let cached = { Quote = quote; ReceivedAt = now }
        
        cache
        |> Map.change quote.Pair (fun existing ->
            let inner = existing |> Option.defaultValue Map.empty
            Some (inner |> Map.add quote.Exchange cached)
        )

    // Pure function to create error status
    let private createErrorStatus (code: string) (message: string) (currentStatus: TradingStatus) : TradingStatus =
        { currentStatus with
            State = Halted FaultyOrder
            Reason = Some FaultyOrder
            LastError = Some {
                Code = code
                Message = message
                At = DateTime.UtcNow
            }
        }

    // Pure function to create running status
    let private createRunningStatus () : TradingStatus =
        {
            State = Running
            Since = DateTime.UtcNow
            Reason = None
            LastError = None
        }

    // Pure function to create halted status
    let private createHaltedStatus (reason: HaltReason) : TradingStatus =
        {
            State = Halted reason
            Since = DateTime.UtcNow
            Reason = Some reason
            LastError = None
        }

    // Async workflow to persist quote
    let private persistQuote (quote: MarketQuote) : Async<unit> =
        async {
            let! result = MarketDataAdapter.persistToMongo quote mongoConn
            
            result
            |> function
                | Ok _ -> ()
                | Error err -> logger $"MongoDB persistence error: {err}"
        }

    // Process quotes: update cache and persist
    let private processQuotes (quotes: MarketQuote list) (cache: Map<string, Map<ExchangeId, CachedQuote>>) : Map<string, Map<ExchangeId, CachedQuote>> =
        quotes
        |> List.fold (fun acc quote ->
            persistQuote quote |> Async.Start
            addQuoteToCache quote acc
        ) cache

    // Handle message result using pattern matching
    let private handleMessageResult 
        (result: Result<string, string>) 
        (cache: Map<string, Map<ExchangeId, CachedQuote>>) 
        (status: TradingStatus) 
        : Result<Map<string, Map<ExchangeId, CachedQuote>> * TradingStatus, TradingStatus> =
        
        result
        |> Result.bind (fun json ->
            MarketDataAdapter.parseMessages json
            |> Result.map (fun quotes ->
                let newCache = processQuotes quotes cache
                (newCache, status)
            )
        )
        |> function
            | Ok (newCache, currentStatus) -> Ok (newCache, currentStatus)
            | Error err ->
                logger $"[ERROR] WebSocket stream error: {err}"
                let errorStatus = createErrorStatus "WEBSOCKET_ERROR" err status
                Error errorStatus

    // Recursive streaming loop with pattern matching
    let rec private streamMarketData 
        (ws: ClientWebSocket) 
        (ct: CancellationToken) 
        (cache: Map<string, Map<ExchangeId, CachedQuote>>) 
        (status: TradingStatus) 
        : Async<unit> =
        
        async {
            match ct.IsCancellationRequested with
            | true -> ()
            | false ->
                let! messageResult = MarketDataAdapter.receiveMessage ws
                
                match handleMessageResult messageResult cache status with
                | Ok (newCache, newStatus) ->
                    lock currentState (fun () ->
                        currentState := { !currentState with Cache = newCache; Status = newStatus }
                    )
                    return! streamMarketData ws ct newCache newStatus
                    
                | Error errorStatus ->
                    lock currentState (fun () ->
                        currentState := { !currentState with Status = errorStatus }
                    )
        }

    // Handle start trading based on current state
    let private handleStartTrading (state: WorkflowState) : Async<Result<TradingStatus, string>> =
        match state.Status.State with
        | Running -> async { return Error "Trading is already running" }
        | _ ->
            async {
                logger $"Trading Start - Begin at {DateTime.UtcNow}"
                let! wsResult = MarketDataAdapter.connectWebSocket "wss://one8656-live-data.onrender.com/"
                
                return
                    wsResult
                    |> function
                        | Ok ws ->
                            let cts = new CancellationTokenSource()
                            let newStatus = createRunningStatus ()
                            
                            lock currentState (fun () ->
                                currentState := {
                                    !currentState with
                                        Status = newStatus
                                        Socket = Some ws
                                        CancellationToken = Some cts
                                }
                            )
                            
                            Async.Start(
                                streamMarketData ws cts.Token state.Cache newStatus,
                                cts.Token
                            )
                            
                            logger $"Trading Start - WebSocket connected at {DateTime.UtcNow}"
                            Ok newStatus
                            
                        | Error err ->
                            let errorStatus = createErrorStatus "CONNECTION_FAILED" err state.Status
                            
                            lock currentState (fun () ->
                                currentState := { !currentState with Status = errorStatus }
                            )
                            
                            Error err
            }

    // Handle stop trading based on current state
    let private handleStopTrading (state: WorkflowState) : Async<Result<TradingStatus, string>> =
        match state.CancellationToken, state.Socket with
        | Some cts, Some ws ->
            async {
                logger $"Trading Stop - Begin at {DateTime.UtcNow}"
                cts.Cancel()
                let! closeResult = MarketDataAdapter.closeWebSocket ws
                
                let newStatus = createHaltedStatus ManualStop
                
                lock currentState (fun () ->
                    currentState := {
                        !currentState with
                            Status = newStatus
                            CancellationToken = None
                            Socket = None
                    }
                )
                
                logger $"Trading Stop - Complete at {DateTime.UtcNow}"
                
                return
                    closeResult
                    |> function
                        | Ok _ -> Ok newStatus
                        | Error _ -> Ok newStatus  // Still return success even if close fails
            }
        | _ -> async { return Error "Trading is not running" }

    // Public API
    let startTrading () : Async<Result<TradingStatus, string>> =
        let state = lock currentState (fun () -> !currentState)
        handleStartTrading state

    let stopTrading () : Async<Result<TradingStatus, string>> =
        let state = lock currentState (fun () -> !currentState)
        handleStopTrading state

    let getStatus () : TradingStatus =
        lock currentState (fun () -> (!currentState).Status)

    let getMarketDataSnapshot () : MarketDataSnapshot =
        lock currentState (fun () ->
            { Timestamp = DateTime.UtcNow; Quotes = (!currentState).Cache }
        )
