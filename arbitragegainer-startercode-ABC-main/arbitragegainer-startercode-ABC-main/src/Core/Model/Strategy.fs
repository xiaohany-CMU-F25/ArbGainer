namespace Model

type TradingStrategy =
    { numCryptoPairs: int
      minSpread: double
      minTransactionProfit: double
      maxTransactionValue: double
      maxTradingValue: double
      initialInvestment: double }
