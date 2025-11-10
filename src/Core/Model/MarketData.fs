namespace Core.Model

open System

/// Represents the trading state of the system
type TradingState =
    | Idle
    | Running
    | Halted of HaltReason

and HaltReason =
    | ManualStop
    | ThresholdReached
    | FaultyOrder

/// Exchange identifier
type ExchangeId = 
    | Bitfinex = 6
    | Bitstamp = 23
    | Kraken = 2

/// Real-time market data quote
type MarketQuote = {
    Pair: string
    Exchange: ExchangeId
    BidPrice: decimal
    BidSize: decimal
    AskPrice: decimal
    AskSize: decimal
    Timestamp: int64
}

/// Trading status information
type TradingStatus = {
    State: TradingState
    Since: DateTime
    Reason: HaltReason option
    LastError: ErrorInfo option
}

and ErrorInfo = {
    Code: string
    Message: string
    At: DateTime
}

/// Market data cache entry
type CachedQuote = {
    Quote: MarketQuote
    ReceivedAt: DateTime
}

/// Market data snapshot for the technical endpoint
type MarketDataSnapshot = {
    Timestamp: DateTime
    Quotes: Map<string, Map<ExchangeId, CachedQuote>>
}