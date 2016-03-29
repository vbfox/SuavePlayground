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

[<AutoOpen>]
module AsyncExtensions =
    open System
    open System.Threading
    open System.Threading.Tasks

    type private ContinuationResult<'a> =
    | Success of value : 'a
    | Failure of ex : exn
    | Cancelled of ex : OperationCanceledException

    type Microsoft.FSharp.Control.Async with
        /// Starts a child computation within an asynchronous workflow.
        /// The child is stopped either when the passed in cancellationToken is signaled (It then return None) or when the parent is cancelled.
        static member TryStartChild<'a> (computation : Async<'a>) cancellationToken = async {
            let! currentCt = Async.CancellationToken
            let linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, currentCt)
    
            let taskContinuation (cont, econt, ccont) (task : Task<'a>) =
                // As we created the CTS we need to dispose it
                linked.Dispose()

                match task.Status with
                | TaskStatus.RanToCompletion -> cont(Some(task.Result))
                | TaskStatus.Faulted -> econt(task.Exception.GetBaseException())
                | TaskStatus.Canceled ->
                    if cancellationToken.IsCancellationRequested then
                        cont(None)
                    else
                        ccont(task.Exception.GetBaseException() :?> System.OperationCanceledException)
                | _ -> failwithf "Unsupported task completion status : %A" task.Status

            return! Async.FromContinuations(fun continuations ->
                let task = Async.StartAsTask(computation, cancellationToken = linked.Token)
                task.ContinueWith(taskContinuation continuations) |> ignore
            )
        }

        /// Run a computation in an async workflow, always running the finally computation afterwards, whatever happens.
        static member TryFinallyAsync<'a> (computation : Async<'a>) (finallyComputation : Async<unit>) =
            let finish (compResult : ContinuationResult<'a>, deferredResult : ContinuationResult<unit>) (cont, econt, ccont) = 
                match (compResult, deferredResult) with
                | (Success x, Success()) -> cont x
                | (Failure compExn, Success()) -> econt compExn
                | (Cancelled compExn, Success()) -> ccont compExn
                | (Success _, Failure deferredExn) -> econt deferredExn
                | (Failure compExn, Failure deferredExn) -> econt <| new AggregateException(compExn, deferredExn)
                | (Cancelled _, Failure deferredExn) -> econt deferredExn
                | (_, Cancelled deferredExn) -> econt <| new Exception("Unexpected cancellation.", deferredExn)

            let startDeferred compResult (cont, econt, ccont) =
                Async.StartWithContinuations(finallyComputation,
                    (fun ()  -> finish (compResult, Success ())  (cont, econt, ccont)),
                    (fun exn -> finish (compResult, Failure exn) (cont, econt, ccont)),
                    (fun exn -> finish (compResult, Cancelled exn) (cont, econt, ccont)))

            let startComp ct (cont, econt, ccont) =
                Async.StartWithContinuations(computation,
                    (fun x  -> startDeferred (Success x)  (cont, econt, ccont)),
                    (fun exn -> startDeferred (Failure exn) (cont, econt, ccont)),
                    (fun exn -> startDeferred (Cancelled exn) (cont, econt, ccont)),
                    ct)
                
            async {
                let! ct = Async.CancellationToken
                return! Async.FromContinuations (startComp ct)
            }

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
    open System.Threading
    open System.Collections.Concurrent

    type ConnectionId = ConnectionId of Guid

    type private AgentMessage =
    | SendText of string
    | SendBinary of byte []
    | Close

    type private Agent = MailboxProcessor<AgentMessage * AsyncReplyChannel<unit>>

    let private agentMain (webSocket : WebSocket) (finishedEvent : ManualResetEvent) (agent : Agent) =
        async {
            let mutable loop = true
        
            let handleOp (op : Sockets.SocketOp<unit>) = async {
                let! opResult = op
                match opResult with
                | Choice1Of2 _ -> return ()
                | Choice2Of2 _ ->
                    loop <- false
                    return ()
            }

            while loop do
                let! (reception, replyChannel) = agent.Receive()
                
                match reception with
                | Close -> loop <- false
                | SendBinary data ->
                    do! handleOp (webSocket.send Opcode.Binary data true)
                | SendText str ->
                    let data = System.Text.Encoding.UTF8.GetBytes(str)
                    do! handleOp (webSocket.send Opcode.Text data true)

                replyChannel.Reply ()

        } |> Async.TryFinallyAsync <|
        async {
            // Always try to send a close when finished
            do! webSocket.send Opcode.Close [||] true |> Async.Ignore
        }  |> Async.TryFinallyAsync <|
        async {
            finishedEvent.Set() |> ignore
        }

    type IConnection =
        abstract member Id : ConnectionId
        abstract member Context : HttpContext
        abstract member SendText : string -> Async<unit>
        abstract member SendBinary : byte[] -> Async<unit>
        abstract member Close : unit -> Async<unit>

    type internal InternalConnection =
        {
            Id : ConnectionId
            Manager : ConnectionManager
            Socket : WebSocket
            Context : HttpContext
            Agent : Agent
            Cancellation : CancellationTokenSource
            MailboxFinished : ManualResetEvent
        }

        member this.SendAndIgnore opcode bs fin = async {
            try
                let! _ = this.Socket.send opcode bs fin
                ()
            with
            | _ -> ()
        }

        interface IConnection with
            member this.Id = this.Id
            member this.Context = this.Context
            member this.SendText (text : string) =
                this.Agent.PostAndAsyncReply (fun r -> SendText text, r)
            
            member this.SendBinary (data : byte[]) =
                this.Agent.PostAndAsyncReply (fun r -> SendBinary data, r)

            member this.Close () =
                this.Agent.PostAndAsyncReply (fun r -> Close, r)

    and ConnectionEvent =
    | Connected
    | TextReceived of text : string
    | BinaryReceived of data : byte[]
    | Closed

    and ConnectionEventHandler = IConnection -> ConnectionEvent -> Async<unit>

    and ConnectionManager(eventHandler : ConnectionEventHandler) as this =
        let connections = new ConcurrentDictionary<ConnectionId, InternalConnection>()
        
        let mkConnection webSocket ctx =
            let mailboxFinished = new ManualResetEvent(false)
            let mailboxCancellation = new CancellationTokenSource()
            let mailbox = new Agent(agentMain webSocket mailboxFinished, cancellationToken = mailboxCancellation.Token)
            {
                Id = ConnectionId (Guid.NewGuid())
                Manager = this
                Socket = webSocket
                Context = ctx
                Agent = mailbox
                Cancellation = mailboxCancellation
                MailboxFinished = mailboxFinished
            }

        let disposeConnection connection = 
            (connection.Agent :> IDisposable).Dispose()
            connection.Cancellation.Dispose()
            connection.MailboxFinished.Dispose()

        let createConnection webSocket ctx =
            let connection = mkConnection webSocket ctx
            
            connections.AddOrUpdate(connection.Id, connection, (fun _ _ -> connection))

        let removeConnection connection = 
            connections.TryRemove(connection.Id) |> ignore

        let websocketApp (webSocket : WebSocket) (ctx : HttpContext) =
            let connection = createConnection webSocket ctx
            socket {
                do! eventHandler connection Connected |> Sockets.SocketOp.ofAsync
                let mutable loop = true
                while loop do
                        
                    let! msg = webSocket.read()
                        
                    match msg with
                    | (Text, data, true) ->
                        let text = System.Text.Encoding.UTF8.GetString(data)
                        do! eventHandler connection (TextReceived text) |> Sockets.SocketOp.ofAsync
                    | (Binary, data, true) ->
                        do! eventHandler connection (BinaryReceived data) |> Sockets.SocketOp.ofAsync
                    | (Ping, _, _) -> do! webSocket.send Pong [||] true
                    | (Opcode.Close, _, _) -> 
                        loop <- false
                    | _ -> ()
            } |> SocketOpUtils.tryFinally <|
            socket {
                printfn "Finallized ?"
                return ()
            } |> Async.TryFinallyAsync <|
            async {
                // Signal the mailbox to end the show
                connection.Cancellation.Cancel()
                // Wait for the mailbox to have sent the 'Close' or at least tryied to
                do! connection.MailboxFinished |> Async.AwaitWaitHandle |> Async.Ignore
                disposeConnection connection
                removeConnection connection
                do! eventHandler connection (Closed)
            }

        let connectionsSeq = seq { for c in connections do yield c.Value }

        let broadcast opcode bs fin =
            for connection in connectionsSeq do
                connection.SendAndIgnore opcode bs fin |> Async.Start

        new() = ConnectionManager(fun _ _ -> async {()})

        member this.WebPart : WebPart = handShake websocketApp

        member this.BroadcastText (text : string) =
            let bytes = System.Text.Encoding.UTF8.GetBytes(text)
            broadcast Text bytes true

        member this.BroadcastBinary (data : byte []) =
            broadcast Binary data true
            (*
        member this.AllConnections
            with get() = connections.Values |> List.ofSeq*)

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

let onWebsocketEvent (connection : IConnection) event = async {
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
