namespace Model

open System

type CrossTradedPairsSnapshot =
    { computedAt: DateTime
      pairs: CurrencyPair list }
