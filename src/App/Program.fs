// src/App/Program.fs
namespace App

open Web.WebApp

open System

module Program =
    // Entry point — chooses mode based on env var or CLI arg
    [<EntryPoint>]
    let main argv =
        let mode =
            match Environment.GetEnvironmentVariable("MODE"), argv with
            | null, [||] -> "web"
            | null, [| m |] -> m.ToLowerInvariant()
            | m, _ when not (System.String.IsNullOrWhiteSpace m) -> m.ToLowerInvariant()
            | _ -> "web"

        match mode with
        | "console" -> runConsole ()
        | "web" -> runWeb ()
        | other ->
            eprintfn "Unknown mode '%s'. Use 'console' or 'web'." other
            1
