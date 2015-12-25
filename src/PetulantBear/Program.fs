﻿module Program

open Suave // always open suave
open Suave.Logging
open Suave.Http
open Suave.Http.Applicatives
open Suave.Http.Successful // for OK-result
open Suave.Web // for config
open System.Configuration

open Akka.Configuration.Hocon
open Akka.FSharp

//*********************
open Suave
open Suave.Types
open Suave.Cookie
open Suave.Auth

open PetulantBear


open System
open System.Net
open System.Collections.Generic
open System.Text.RegularExpressions


//let events = new List<Event<Games.Events>>()
//let saveEvents streamName (id, expectedVersion, evt) =
//    
//    events.Add({ id= id; version=expectedVersion;payLoad= evt })


//    serverKey             = Utils.Crypto.generateKey HttpRuntime.ServerKeyLength
//    errorHandler          = defaultErrorHandler
//    listenTimeout         = TimeSpan.FromMilliseconds 2000.
//    cancellationToken     = Async.DefaultCancellationToken
//    bufferSize            = 2048
//    maxOps                = 100
//    mimeTypesMap          = Writers.defaultMimeTypesMap
//    homeFolder            = None
//    compressedFilesFolder = None
//    logger                = logger
//    cookieSerialiser = 



open Suave.Logging
open Logary
open Logary.Configuration
open Logary.Targets
open Logary.Metrics
open NodaTime

open EventStore.ClientAPI.Embedded
open System.Threading
open EventStore.Core.Bus
open EventStore.Core.Messages

type EmbeddedEventStore() =
    let node = EmbeddedVNodeBuilder.AsSingleNode()
                                   .OnDefaultEndpoints()
                                  .WithExternalTcpOn(new Net.IPEndPoint(Net.IPAddress.Parse("127.0.0.1"),1789))
                                  .WithInternalTcpOn(new Net.IPEndPoint(Net.IPAddress.Parse("127.0.0.1"),1790))
                                  .WithExternalHttpOn(new Net.IPEndPoint(Net.IPAddress.Parse("127.0.0.1"),1791))
                                  .WithInternalHttpOn(new Net.IPEndPoint(Net.IPAddress.Parse("127.0.0.1"),1792))
                                  .RunInMemory()
                                  .RunProjections(ProjectionsMode.All)
                                  .WithWorkerThreads(16)
                                  .Build()

    member this.start() =
        printfn "starting embedded EventStore"
        node.Start()

    member this.stop() =
        let stopped = new AutoResetEvent(false)
        node.MainBus.Subscribe( new AdHocHandler<SystemMessage.BecomeShutdown>(fun m -> stopped.Set() |> ignore))
        node.Stop() |> ignore
        if not (stopped.WaitOne(20000)) then  printfn "couldn't stop ES within 20000 ms"
        else printfn "stopped embedded EventStore"


[<EntryPoint>]
let main args =

    let rootPath = ConfigurationManager.AppSettings.["rootPath"]
    let ipAddress = ConfigurationManager.AppSettings.["IPAddress"]
    let port = Int32.Parse( ConfigurationManager.AppSettings.["Port"])
    let urlSite = ConfigurationManager.AppSettings.["urlSite"]
    let eventStoreConnectionString = ConfigurationManager.AppSettings.["eventStoreConnectionString"]
    let couldParseIsEmbedded,isEmbedded = bool.TryParse(ConfigurationManager.AppSettings.["isEmbedded"])
    let dbConnection = ConfigurationManager.ConnectionStrings.["bear2bearDB"].ConnectionString


    let confElmah :Logary.Targets.ElmahIO.ElmahIOConf =
        match Guid.TryParse(ConfigurationManager.AppSettings.Get("elmah.io")) with
        | true, logId ->{ logId = logId; }
        | false, _->{ logId = Guid.Empty; }
     
    
    use logary =
      withLogary' "bear2bear.web" (
        withTargets [
          Console.create Console.empty "console"
          Logary.Targets.ElmahIO.create  confElmah "elmah"

        ] >>
          withRules [
            Rule.create (Regex(".*", RegexOptions.Compiled)) "console" (fun _ -> true) (fun _ -> true) Info
            Rule.create (Regex(".*", RegexOptions.Compiled)) "elmah" (fun _ -> true) (fun _ -> true) Error
          ]
        )
    
    
    let section = ConfigurationManager.GetSection("akka"):?> AkkaConfigurationSection
    
    let repo = (EventSourceRepo.create dbConnection eventStoreConnectionString)
    (repo:>IEventStoreRepository).connect()

    

    let system = System.create "System" ( section.AkkaConfig)
    let config = 
        { defaultConfig with 
            logger = SuaveAdapter(logary.GetLogger "suave")
            bindings = [ HttpBinding.mk' HTTP ipAddress port ] 
            homeFolder = Some(rootPath)
        }

    let startPetulant = 
        let httpendPoint = new IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"), 2113);
        let catchupProjection = PetulantBear.Projections.CatchUp.create (repo:>IEventStoreProjection)  dbConnection httpendPoint

        let projections = [
                (PetulantBear.Projections.Games.name,PetulantBear.Projections.Games.projection)
                (PetulantBear.Projections.Room.name,PetulantBear.Projections.Room.projection)
                (PetulantBear.Projections.Cleaveage.name,PetulantBear.Projections.Cleaveage.projection)
            ]
        
        let subsciptions =
            projections
            |> List.fold (fun agg (name,projection) -> 
                    catchupProjection.createProjectionAsync(name) |> Async.RunSynchronously        
                    let s = catchupProjection.startProjection(name,projection)
                    s::agg
                ) []
        //subscription.Stop()
        (PetulantBear.Application.app rootPath urlSite system repo Users.authenticateWithLogin)
        |> startWebServer config
    
    if isEmbedded or not <| couldParseIsEmbedded then
        let embeddedEventStore = new EmbeddedEventStore()
        embeddedEventStore.start()

        startPetulant

        embeddedEventStore.stop()
    else startPetulant
    0
