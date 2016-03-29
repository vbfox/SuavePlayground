[<AutoOpen>]
module AsyncExtensions =
    open System.Threading
    open System.Threading.Tasks

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

open System.Threading
open System.Threading.Tasks


let readFromSocket : Async<int> = async {
    printfn "readFromSocket start"
    do! Async.Sleep 2000
    printfn "readFromSocket end"
    return 1
}

let readLoop externalCancellation = async {
    let! currentCt = Async.CancellationToken
    currentCt.Register(fun _ -> printfn "readLoop Async token cancelled") |> ignore

    let mutable loop = true
    
    while loop do
        let! r = Async.TryStartChild readFromSocket externalCancellation
        match r with
        | Some i -> printfn "Read %i" i
        | None ->
            printfn "Externally cancelled, stopping"
            loop <- false
}

let cts = new CancellationTokenSource()
let taskCts = new CancellationTokenSource()
printfn "Start as task"
let task = Async.StartAsTask(readLoop cts.Token, cancellationToken = taskCts.Token)
printfn "Sleep"
Thread.Sleep(3000)
printfn "Cancel token"
//cts.Cancel()
taskCts.Cancel()
printfn "Waiting for task end"
try
    task.Wait()
with | ex -> printfn "Task ended with exception"
printfn "End"
