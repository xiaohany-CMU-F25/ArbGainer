namespace Logging

open System

module Logger =
    let createLogger () =
        fun (message: string) ->
            let timestamp = DateTimeOffset.UtcNow.ToString("O")
            printfn "[%s] %s" timestamp message
