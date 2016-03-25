open Suave
open Suave.Files
open Suave.Http
open Suave.Logging
open Suave.Operators
open Suave.RequestErrors
open Suave.Sockets.Control
open Suave.WebSocket
open Suave.Filters
open System.IO

let websocketApp (webSocket : WebSocket) (_ : HttpContext) = 
    socket { 
        let mutable loop = true
        while loop do
            let! msg = webSocket.read()
            match msg with
            | (Text, data, true) -> 
                do! webSocket.send Text data true
            | (Ping, _, _) -> do! webSocket.send Pong [||] true
            | (Close, _, _) -> 
                do! webSocket.send Close [||] true
                loop <- false
            | _ -> ()
    }

let app : WebPart =
  choose
    [
        path "/websocket" >=> handShake websocketApp
        GET >=> choose
            [
                path "/" >=> browseFileHome "index.html"
                browseHome
            ]
        NOT_FOUND "Found no handlers"
    ]

[<EntryPoint>]
let main _ = 
    let config =
        { defaultConfig with
            homeFolder = Some <| Path.GetFullPath("../../../Html/")
            logger = Loggers.saneDefaultsFor LogLevel.Verbose
            }
    startWebServer config app
    0
