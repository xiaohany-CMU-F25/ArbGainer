namespace Infrastructure.Adapters

open System
open Model

module CrossTradedPairsStore =
    type StorageSnapshot =
        { ComputedAt: DateTime
          Pairs: string list }

    type InMemoryCrossTradedPairsRepository() =
        let gate = obj()
        let mutable snapshot: StorageSnapshot option = None

        member _.SaveSnapshot(domainSnapshot: CrossTradedPairsSnapshot) =
            async {
                try
                    lock gate (fun () ->
                        let serializedPairs =
                            domainSnapshot.pairs
                            |> List.map CurrencyPair.toStorageKey

                        snapshot <-
                            Some
                                { ComputedAt = domainSnapshot.computedAt
                                  Pairs = serializedPairs })

                    return Ok ()
                with ex ->
                    return Error(RepositoryError $"Unable to store cross-traded pairs: {ex.Message}")
            }

        member _.GetLatestSnapshot() =
            async {
                try
                    let stored = lock gate (fun () -> snapshot)

                    match stored with
                    | None -> return Ok None
                    | Some saved ->
                        let pairs =
                            saved.Pairs
                            |> List.choose (fun key ->
                                match CurrencyPair.tryFromStorageKey key with
                                | Ok pair -> Some pair
                                | Error _ -> None)

                        let domainSnapshot: CrossTradedPairsSnapshot =
                            { computedAt = saved.ComputedAt
                              pairs = pairs }

                        return Ok(Some domainSnapshot)
                with ex ->
                    return Error(RepositoryError $"Unable to read cross-traded pairs: {ex.Message}")
            }

    type Repository =
        { save: CrossTradedPairsSnapshot -> Async<Result<unit, DomainError>>
          getLatest: unit -> Async<Result<CrossTradedPairsSnapshot option, DomainError>> }

    let createInMemoryRepository () =
        let repository = InMemoryCrossTradedPairsRepository()

        { save = repository.SaveSnapshot
          getLatest = repository.GetLatestSnapshot }
