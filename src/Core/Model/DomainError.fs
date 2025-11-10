namespace Model

type Exchange =
    | Bitfinex
    | Bitstamp
    | Kraken

type DomainError =
    | ValidationError of string
    | ExternalDependencyError of Exchange * string
    | RepositoryError of string
