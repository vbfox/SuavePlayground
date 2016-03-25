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

module SocketOpUtils = 
    let tryFinally (body: Sockets.SocketOp<'x>) (finalize: Sockets.SocketOp<unit>) : Sockets.SocketOp<'x> = async {
        let! result = Async.Catch body
        let! finallyResult = finalize
        return
            match finallyResult with
            | Choice1Of2 _ ->
                match result with
                | Choice1Of2 result -> result
                | Choice2Of2 ex -> raise ex
            | Choice2Of2 error -> Choice2Of2 error
    }

module SignalS =
    open System
    open SocketOpUtils
    open System.Collections.Concurrent

    type ConnectionId = ConnectionId of Guid

    type Connection =
        {
            Id : ConnectionId
            Manager : ConnectionManager
            Socket : WebSocket
            Context : HttpContext
        }
        member this.SendText (text : string) =
            let bytes = System.Text.Encoding.UTF8.GetBytes(text)
            this.Socket.send Text bytes true
            
        member this.SendBinary (data : byte[]) =
            this.Socket.send Binary data true

        member this.SendAndIgnore opcode bs fin = async {
            try
                let! _ = this.Socket.send opcode bs fin
                ()
            with
            | _ -> ()
        }

    and ConnectionEvent =
    | Connected
    | TextReceived of text : string
    | BinaryReceived of data : byte[]
    | Closed

    and ConnectionEventHandler = Connection -> ConnectionEvent -> Sockets.SocketOp<unit>

    and ConnectionManager(eventHandler : ConnectionEventHandler) as this =
        let connections = new ConcurrentDictionary<ConnectionId, Connection>()

        let mkConnection webSocket ctx =
            { Id = ConnectionId (Guid.NewGuid()); Manager = this; Socket = webSocket; Context = ctx }

        let createConnection webSocket ctx =
            let connection = mkConnection webSocket ctx
            connections.AddOrUpdate(connection.Id, connection, (fun _ _ -> connection))

        let removeConnection connection = 
            connections.TryRemove(connection.Id) |> ignore

        let websocketApp (webSocket : WebSocket) (ctx : HttpContext) =
            let connection = createConnection webSocket ctx
            tryFinally
                (socket {
                    do! eventHandler connection Connected
                    let mutable loop = true
                    while loop do
                        let! msg = webSocket.read()
                        match msg with
                        | (Text, data, true) ->
                            let text = System.Text.Encoding.UTF8.GetString(data)
                            do! eventHandler connection (TextReceived text)
                        | (Binary, data, true) ->
                            do! eventHandler connection (BinaryReceived data)
                        | (Ping, _, _) -> do! webSocket.send Pong [||] true
                        | (Close, _, _) -> 
                            do! webSocket.send Close [||] true
                            loop <- false
                        | _ -> ()
                })
                (socket {
                    removeConnection connection
                    do! eventHandler connection (Closed)
                })

        let connectionsSeq = seq { for c in connections do yield c.Value }

        let broadcast opcode bs fin =
            for connection in connectionsSeq do
                connection.SendAndIgnore opcode bs fin |> Async.Start

        new() = ConnectionManager(fun _ _ -> socket {()})

        member this.WebPart : WebPart = handShake websocketApp

        member this.BroadcastText (text : string) =
            let bytes = System.Text.Encoding.UTF8.GetBytes(text)
            broadcast Text bytes true

        member this.BroadcastBinary (data : byte []) =
            broadcast Binary data true

        member this.AllConnections
            with get() = connections.Values |> List.ofSeq

    let pullWebSocket handler =
        let manager = new ConnectionManager(handler)
        manager.WebPart

    let pushWebSocket () =
        new ConnectionManager()

    let bidirectionalWebSocket handler =
        let manager = new ConnectionManager(handler)
        manager

open SignalS

let app (connectionManager : ConnectionManager) =
  choose
    [
        path "/websocket" >=> connectionManager.WebPart
        GET >=> choose
            [
                path "/" >=> browseFileHome "index.html"
                browseHome
            ]
        NOT_FOUND "Found no handlers"
    ]

let onWebsocketEvent connection event = socket {
    match event with
    | Connected ->
        printfn "We have a new client from %O" connection.Context.connection.ipAddr
        return! connection.SendText "Hello new client"
    | TextReceived text -> return! connection.SendText text
    | Closed ->
        printfn "We lost a client from %O" connection.Context.connection.ipAddr
    | _ -> ()
}

[<EntryPoint>]
let main _ = 
    let config =
        { defaultConfig with
            homeFolder = Some <| Path.GetFullPath("../../../Html/")
            logger = Loggers.saneDefaultsFor LogLevel.Verbose
            }

    let webSocket = bidirectionalWebSocket onWebsocketEvent

    let timer = new System.Timers.Timer(1000.)
    timer.Elapsed.Add(fun _ -> webSocket.BroadcastText (sprintf "It is %s" (System.DateTimeOffset.Now.ToString())))
    timer.Start ()

    startWebServer config (app webSocket)
    0
