﻿namespace CartPole.ApeXDQN
open System.Threading.Tasks
open FSharp.Control.TaskBuilder
open FSharp.Control.TaskBuilderExtensions
open DiffSharp
open DiffSharp.Model
open DiffSharp.Compose
open DiffSharp.Optim
 
open FSharp.Control
open FSharp.Control.Reactive
open FSharp.Control.Reactive.Builders

open Plotly.NET
 
type Learner(globalNet:QNetwork, actors:Actor[], discount:float, learningRate:float)=  
    let optimizer = Adam(globalNet,lr=dsharp.tensor learningRate) 
    member _.UpdateNetwork(minibatchs: (int[]* float[]*Transitions)[]) =
        let indicesAll = ResizeArray<int>()
        let tdErrorsAll = ResizeArray<float>()
        for indices, weights, transitions in minibatchs do 
            let states = transitions.states     |> dsharp.stack 
            let acts = transitions.actions     |> dsharp.stack 
            let rewards = transitions.rewards  |> dsharp.stack |> dsharp.unsqueeze 1 
            let nextStates = transitions.nextStates  |> dsharp.stack 
            let isDones = transitions.isDones  |> dsharp.stack|> dsharp.unsqueeze 1 
            
            globalNet.reverseDiff()
            let Q = 
                (states --> globalNet).mul(acts)

            let TQ  = 
                let nextStateValues =  
                    let am = (nextStates --> globalNet).argmax(1).view [-1;1]    
                    (nextStates --> globalNet).gather(1,am)
                (rewards + discount * (1 - isDones) * nextStateValues ).mul(acts)  

            let tdErrors = (TQ - Q)**2
            let loss =
                dsharp.mean ( (dsharp.tensor (weights  |> Array.map(fun t -> [t;t]))).mul tdErrors)
                
            loss.reverse()
            optimizer.step() 
            globalNet.noDiff()
            
            indicesAll.AddRange(indices) 
            tdErrorsAll.AddRange( 
                let am = tdErrors.argmax(1).view([-1;1])
                tdErrors.gather(1,am).flatten().toArray() :?> float32[] |> Array.map float
            ) 
        ( indicesAll.ToArray(), tdErrorsAll.ToArray())


     

    member this.Learn()=  
        let replay = Replay(bufferSize= (1<<<14)) 
        let miniN = 16 
        let rollOut i =  
            task {  
                let (tdError, (obss, acts, nxtObss, rewards, isDones)) = 
                    actors[i].RollOut(globalNet.parameters) 
                return (tdError, (obss, acts, nxtObss, rewards, isDones))  
            } :> Task

        let rollOuts = actors |> Array.mapi(fun i _ -> rollOut i) 
                 
        for _ in 0..30 do 
            // https://stackoverflow.com/questions/5116712/task-waitall-on-a-list-in-f
            let id = Task.WaitAny rollOuts
            let (tdError, (obss, acts, nxtObss, rewards, isDones)) = 
                rollOuts[id] 
                :?> Task<float[] * (Tensor[]*Tensor[]*Tensor[]*Tensor[]*Tensor[])>
                |> fun t -> t.Result
            replay.Add (tdError, obss, acts, nxtObss, rewards, isDones)  
            rollOuts[id] <- rollOut id
            
        let learn (replay: Replay) = 
            task { 
                let minibatchs = Array.init miniN (fun _ -> replay.SampleMinibatch(batchSize=32)) 
                let indices, tdErrors = this.UpdateNetwork(minibatchs)  
                return indices, tdErrors
            }
        let mutable learner = learn replay 
        let mutable updateCnt = 0
        let iter = 300
        while updateCnt < iter do
            System.Console.CursorLeft <- 0
            printf  "%A / %A" updateCnt iter
            let id = Task.WaitAny rollOuts
            let (tdError, (obss, acts, nxtObss, rewards, isDones)) = 
                rollOuts[id] 
                :?> Task<float[] * (Tensor[]*Tensor[]*Tensor[]*Tensor[]*Tensor[])>
                |> fun t -> t.Result
            replay.Add (tdError, obss, acts, nxtObss, rewards, isDones)  
            rollOuts[id] <- rollOut id 
            if learner.IsCompleted then  
                replay.UpdatePriority learner.Result
                updateCnt <- updateCnt + 1  
                learner <- learn replay
                  
        Chart.Line(xy = actors[0].Log )
        |> Chart.show 