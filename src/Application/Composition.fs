namespace Application

open System
open System.Net.Http
open Model
open Services.CrossTradedPairsWorkflow
open Infrastructure.Adapters
open Infrastructure.Adapters.ExchangeClients

type CrossTradedPairsApi =
    { IdentificationMetricName: string
      RetrievalMetricName: string
      Refresh: (string -> unit) -> Async<Result<CrossTradedPairsSnapshot, DomainError>>
      GetLatest: (string -> unit) -> Async<Result<CrossTradedPairsSnapshot option, DomainError>> }

type ApplicationServices =
    { CrossTradedPairs: CrossTradedPairsApi }

module CompositionRoot =
    let createServices () =
        let httpClient = new HttpClient()
        httpClient.Timeout <- TimeSpan.FromSeconds 10.0

        let repository = CrossTradedPairsStore.createInMemoryRepository ()

        let dependencies: Dependencies =
            { Providers =
                [ (Exchange.Bitfinex, createBitfinexFetcher httpClient)
                  (Exchange.Bitstamp, createBitstampFetcher httpClient)
                  (Exchange.Kraken, createKrakenFetcher httpClient) ]
              Repository =
                { SaveSnapshot = repository.save
                  GetLatestSnapshot = repository.getLatest } }

        { CrossTradedPairs =
            { IdentificationMetricName = IdentificationMetricName
              RetrievalMetricName = RetrievalMetricName
              Refresh = refresh dependencies
              GetLatest = getLatest dependencies } }
