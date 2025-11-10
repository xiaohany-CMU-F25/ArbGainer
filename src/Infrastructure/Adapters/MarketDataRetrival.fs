namespace Infrastructure.Adapters

open System
open System.Net.WebSockets
open System.Text
open System.Threading
open FSharp.Data
open Core.Model
open MongoDB.Driver

module MarketDataAdapter =

    type WebSocketMessage = JsonProvider<"""
        [{"ev":"XQ","pair":"SOL-USD","x":23,"bp":28.05,"bs":19.98,"ap":28.07,"as":20.12,"t":1701197108733}]
    """>

    type ConnectionStatus =
        | Connected of ClientWebSocket
        | Disconnected
        | Failed of exn

    let private parseExchangeId (id: int) : ExchangeId option =
        match id with
        | 6 -> Some ExchangeId.Bitfinex
        | 23 -> Some ExchangeId.Bitstamp
        | 2 -> Some ExchangeId.Kraken
        | _ -> None

    let private parseQuote (json: WebSocketMessage.Root) : MarketQuote option =
        parseExchangeId json.X
        |> Option.bind (fun exchangeId ->
            async {
                let! result =
                    async {
                        return {
                            Pair = json.Pair
                            Exchange = exchangeId
                            BidPrice = decimal json.Bp
                            BidSize = decimal json.Bs
                            AskPrice = decimal json.Ap
                            AskSize = decimal json.As
                            Timestamp = json.T
                        }
                    }
                    |> Async.Catch
                return
                    result
                    |> function
                        | Choice1Of2 quote -> Some quote
                        | Choice2Of2 _ -> None
            }
            |> Async.RunSynchronously
        )

    let connectWebSocket (url: string) : Async<Result<ClientWebSocket, string>> =
        async {
            let! result =
                async {
                    let ws = new ClientWebSocket()
                    let uri = Uri(url)
                    do! ws.ConnectAsync(uri, CancellationToken.None) |> Async.AwaitTask
                    return Ok ws
                }
                |> Async.Catch
            
            return
                result
                |> function
                    | Choice1Of2 wsResult -> wsResult
                    | Choice2Of2 ex -> Error $"WebSocket connection failed: {ex.Message}"
        }

    let receiveMessage (ws: ClientWebSocket) : Async<Result<string, string>> =
        async {
            let! result =
                async {
                    let buffer = Array.zeroCreate 4096
                    let segment = ArraySegment<byte>(buffer)
                    let! wsResult = ws.ReceiveAsync(segment, CancellationToken.None) |> Async.AwaitTask

                    return
                        match wsResult.MessageType with
                        | WebSocketMessageType.Close -> Error "WebSocket closed by server"
                        | _ ->
                            let message = Encoding.UTF8.GetString(buffer, 0, wsResult.Count)
                            Ok message
                }
                |> Async.Catch
            
            return
                result
                |> function
                    | Choice1Of2 msgResult -> msgResult
                    | Choice2Of2 ex -> Error $"Failed to receive message: {ex.Message}"
        }

    let parseMessages (jsonText: string) : Result<MarketQuote list, string> =
        async {
            let! result =
                async {
                    let messages = WebSocketMessage.Parse(jsonText)
                    return
                        messages
                        |> Array.choose (fun msg ->
                            // Check if message has required fields
                            match msg.JsonValue.TryGetProperty("x") with
                            | Some _ -> parseQuote msg
                            | None -> None  // Skip status messages
                        )
                        |> Array.toList
                        |> Ok
                }
                |> Async.Catch
            
            return
                result
                |> function
                    | Choice1Of2 quotes -> quotes
                    | Choice2Of2 ex -> Error $"Failed to parse messages: {ex.Message}"
        }
        |> Async.RunSynchronously

    let closeWebSocket (ws: ClientWebSocket) : Async<Result<unit, string>> =
        async {
            let! result =
                async {
                    return!
                        match ws.State with
                        | WebSocketState.Open ->
                            async {
                                do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None)
                                    |> Async.AwaitTask
                                ws.Dispose()
                                return Ok ()
                            }
                        | _ ->
                            async {
                                ws.Dispose()
                                return Ok ()
                            }
                }
                |> Async.Catch
            
            return
                result
                |> function
                    | Choice1Of2 closeResult -> closeResult
                    | Choice2Of2 ex -> Error $"Failed to close WebSocket: {ex.Message}"
        }

    let persistToMongo (quote: MarketQuote) (connectionString: string) : Async<Result<unit, string>> =
        async {
            let! result =
                async {
                    let client = MongoClient(connectionString)
                    let url = MongoUrl(connectionString)
                    let dbName = 
                        match url.DatabaseName with
                        | null | "" -> "arbitrage"
                        | name -> name
                    
                    let db = client.GetDatabase(dbName)
                    let collection = db.GetCollection<MarketQuote>("quotes")
                    do! collection.InsertOneAsync(quote) |> Async.AwaitTask
                    return Ok ()
                }
                |> Async.Catch
            
            return
                result
                |> function
                    | Choice1Of2 insertResult -> insertResult
                    | Choice2Of2 ex -> Error $"MongoDB insert failed: {ex.Message}"
        }
