module GlobalPollenProject.App.UseCases

open System
open System.IO
open System.Linq
open System.Threading
open Microsoft.Extensions.Configuration

open GlobalPollenProject.Core.Aggregate
open GlobalPollenProject.Core.DomainTypes
open GlobalPollenProject.Core.Dependencies
open GlobalPollenProject.Core.Composition

open ReadModels
open ReadStore
open Converters
open Responses

type GetCurrentUser = unit -> Guid

let mutable inMaintainanceMode = false

// Load AppSettings
let appSettings = ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build()
let getAppSetting name =
    match String.IsNullOrEmpty appSettings.[name] with
    | true -> invalidOp "Appsetting is missing: " + name
    | false -> appSettings.[name]

// Communication
let sendEmail correspondingEmail subject messageHtml = 
    EmailSender.Cloud.send (getAppSetting "SendGridKey") (getAppSetting "EmailFromName") (getAppSetting "EmailFromAddress") correspondingEmail subject messageHtml

// Image Store
let saveImage = AzureImageStore.uploadToAzure (getAppSetting "imagestore:baseurl") (getAppSetting "imagestore:container") (getAppSetting "imagestore:azureconnectionstring") (fun x -> Guid.NewGuid().ToString())
let generateCacheImage = AzureImageStore.generateCacheImage (getAppSetting "imagestore:container") (getAppSetting "imagestore:cachecontainer") (getAppSetting "imagestore:azureconnectionstring")
let toAbsoluteUrl = Url.relativeToAbsolute appSettings.["imagestore:baseurl"]

// Write (Event) Store
let eventStore = lazy(
    let ip = appSettings.["eventstore:eventstoreip"]
    let port = appSettings.["eventstore:eventstoreport"] |> int
    let username = appSettings.["eventstore:eventstoreuser"]
    let pass = appSettings.["eventstore:eventstorepassword"]
    let es = EventStore.connect ip port username pass |> Async.RunSynchronously
    EventStore.EventStore(es) )

// Read Model 'Repository'
let readStoreGet,readStoreGetList,readStoreGetSortedList,readLex,redisSet,redisSetList,redisSetSortedList,redisReset =
    let ip = appSettings.["readstore:redisip"]
    let redis = lazy (ReadStore.Redis.connect ip)
    redis.Value |> ReadStore.Redis.get, 
    redis.Value |> ReadStore.Redis.getListItems, 
    redis.Value |> ReadStore.Redis.getSortedListItems, 
    redis.Value |> ReadStore.Redis.lexographicSearch,
    redis.Value |> ReadStore.Redis.set, 
    redis.Value |> ReadStore.Redis.addToList,
    redis.Value |> ReadStore.Redis.addToSortedList,
    redis.Value |> ReadStore.Redis.reset

let inline deserialise< ^a> json = 
    let unwrap (ReadStore.Json j) = j
    Serialisation.deserialise< ^a> (unwrap json)

let serialise s = 
    let result = Serialisation.serialise s
    match result with
    | Ok r -> Ok <| ReadStore.Json r
    | Error e -> Error e

let projectionHandler e =
    let router = ProjectionHandler.route readStoreGet readStoreGetList readStoreGetSortedList redisSet redisSetList redisSetSortedList generateCacheImage toAbsoluteUrl
    let getEventCount = eventStore.Value.Checkpoint
    let result = (ProjectionHandler.readModelAgent router readStoreGet redisSet getEventCount).PostAndReply(fun rc -> e, rc)
    match result with 
    | Ok r -> ()
    | Error e -> invalidOp ("Read model is corrupt: " + e)

eventStore.Value.SaveEvent 
:> IObservable<string*obj*DateTime>
|> Observable.subscribe projectionHandler
|> ignore

let private deserialiseGuid json =
    let unwrap (ReadStore.Json j) = j
    let s = (unwrap json).Replace("\"", "")
    match Guid.TryParse(s) with
    | true,g -> Ok g
    | false,g -> Error <| "Guid was not in correct format"


// App Core Dependencies
let domainDependencies = 
    let log = ignore
    let calculateIdentity = calculateTaxonomicIdentity ReadStore.TaxonomicBackbone.findMatches
    let isValidTaxon query =
        match TaxonomicBackbone.validate query readStoreGet readStoreGetSortedList deserialise with
        | Ok t -> Some (TaxonId t.Id)
        | Error _ -> None

    let neotomaLink id = 
        TaxonomicBackbone.getById id readStoreGet deserialise
        |> bind ExternalLink.toLinkRequest
        |> lift ExternalLink.getNeotomaId
    let gbifLink id =
        TaxonomicBackbone.getById id readStoreGet deserialise
        |> bind ExternalLink.toLinkRequest
        |> lift ExternalLink.getGbifId
    let eolLink id =
        TaxonomicBackbone.getById id readStoreGet deserialise
        |> bind ExternalLink.toLinkRequest
        |> lift ExternalLink.getEncyclopediaOfLifeId

    { GenerateId          = Guid.NewGuid
      Log                 = log
      GetGbifId           = gbifLink
      GetNeotomaId        = neotomaLink
      GetEolId            = eolLink
      GetTime             = (fun _ -> DateTime.Now)
      ValidateTaxon       = isValidTaxon
      CalculateIdentity   = calculateIdentity }


let toAppResult domainResult =
    match domainResult with
    | Ok r -> Ok r
    | Error _ -> Error Core

let toNotFoundResult domainResult =
    match domainResult with
    | Ok r -> Ok r
    | Error _ -> Error NotFound

let toPersistenceError domainResult =
    match domainResult with
    | Ok r -> Ok r
    | Error _ -> Error Persistence

let toValidationError domainResult =
    match domainResult with
    | Ok r -> Ok r
    | Error str -> Error <| Validation [{ Property = ""; Errors = [str]}]


module Digitise =

    open GlobalPollenProject.Core.Aggregates.ReferenceCollection
    open Converters
   
    let myCollections getCurrentUser = 
        let userId = getCurrentUser()
        let cols = ReadStore.RepositoryBase.getListKey<Guid> All ("CollectionAccessList:" + (userId.ToString())) readStoreGetList deserialiseGuid
        match cols with
        | Error e -> Error Persistence
        | Ok clist -> 
            let getCol id = ReadStore.RepositoryBase.getSingle<EditableRefCollection> (id.ToString()) readStoreGet deserialise
            clist 
            |> List.map getCol 
            |> List.choose (fun r -> match r with | Ok c -> Some c | Error e -> None)
            |> Ok

    let getCollection id =
        ReadStore.RepositoryBase.getSingle id readStoreGet deserialise<EditableRefCollection>
        |> toAppResult

    let private issueCommand = 
        let aggregate = { initial = State.Initial; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "ReferenceCollection" aggregate domainDependencies

    let startNewCollection getCurrentUser (request:StartCollectionRequest) =
        let newId = CollectionId <| domainDependencies.GenerateId()
        let currentUser = UserId <| getCurrentUser()
        let access = Converters.Metadata.createAccess request.AccessMethod request.Institution request.InstitutionUrl
        let curator = Converters.Metadata.createCurator request.CuratorFirstNames request.CuratorSurname request.CuratorEmail
        let curatorCommand c a = SpecifyCurator (newId, c, a)

        let issueCommands newCol curator =
            newCol |> issueCommand
            curator |> issueCommand
            Ok newId

        let newColCommand = 
            CreateCollection { Id = newId; Name = request.Name; Owner = currentUser; Description = request.Description }

        let curCommand =
            curatorCommand
            <!> curator
            <*> access
        issueCommands newColCommand
        <!> curCommand

    let publish getCurrentUser colId =
        let currentUser = UserId <| getCurrentUser()
        let id = CollectionId colId
        Publish id
        |> issueCommand

    let addSlideRecord request = 
        request
        |> Dto.toAddSlideCommand readStoreGet
        |> lift issueCommand

    let voidSlide (req:VoidSlideRequest) =
        let slideId = SlideId(req.CollectionId |> CollectionId, req.SlideId)
        VoidSlide slideId 
        |> issueCommand
        Ok()

    let uploadSlideImage (request:SlideImageRequest) = 
        request
        |> Converters.Dto.toAddSlideImageCommand readStoreGet saveImage
        |> lift issueCommand
        |> toAppResult


module Calibrations =

    open GlobalPollenProject.Core.Aggregates.Calibration
    open Converters
    let private issueCommand = 
        let aggregate = { initial = State.Initial; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "Calibration" aggregate domainDependencies

    let private deserialiseGuid json =
        let unwrap (ReadStore.Json j) = j
        let s = (unwrap json).Replace("\"", "")
        match Guid.TryParse(s) with
        | true,g -> Ok g
        | false,g -> Error <| "Guid was not in correct format"

    let getMyCalibrations getCurrentUser =
        let userId = getCurrentUser()
        let cols = ReadStore.RepositoryBase.getListKey<Guid> All ("Calibration:User:" + (userId.ToString())) readStoreGetList deserialiseGuid
        match cols with
        | Error e -> Error Persistence
        | Ok clist -> 
            let getCol id = ReadStore.RepositoryBase.getSingle<ReadModels.Calibration> (id.ToString()) readStoreGet deserialise
            clist 
            |> List.map getCol 
            |> List.choose (fun r -> match r with | Ok c -> Some c | Error e -> None)
            |> Ok

    let setupMicroscope getCurrentUser (req:AddMicroscopeRequest) =
        let microscope = Microscope.Light <| LightMicroscope.Compound (10, [ 10; 20; 40; 100 ], "Nikon")
        let name = req.Name |> ShortText.create
        let cmd = UseMicroscope { Id = CalibrationId <| domainDependencies.GenerateId()
                                  User = getCurrentUser() |> UserId
                                  FriendlyName = req.Name
                                  Microscope = microscope }
        issueCommand cmd
        |> Ok

    let calibrateMagnification (req:CalibrateRequest) =
        let getUrl img =
            match img with
            | SingleImage (u,cal) -> Ok u
            | FocusImage _ -> Error "Cannot use focus images"
        let floatingCalibration = {
            Point1 = req.X1,req.Y1
            Point2 = req.X2,req.Y2
            MeasuredDistance = req.MeasuredLength * 1.<um>
        }
        let id = req.CalibrationId |> CalibrationId
        let generateCommand url =
            Calibrate (id,req.Magnification * 1<timesMagnified>, { Image = url ; 
                                                 StartPoint = floatingCalibration.Point1; 
                                                 EndPoint = floatingCalibration.Point2; 
                                                 MeasureLength = floatingCalibration.MeasuredDistance })

        ImageForUpload.Single ((Base64Image req.ImageBase64),floatingCalibration)
        |> saveImage
        |> bind getUrl
        |> lift generateCommand
        |> lift issueCommand
        |> Ok


module UnknownGrains =

    open GlobalPollenProject.Core.Aggregates.Grain

    let private issueCommand = 
        let aggregate = { initial = State.InitialState; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "Specimen" aggregate domainDependencies

    let submitUnknownGrain getCurrentUser (request:AddUnknownGrainRequest) =
        let id = domainDependencies.GenerateId() |> GrainId
        let user = getCurrentUser() |> UserId
        request
        |> Dto.toSubmitUnknownGrain id user saveImage
        |> lift issueCommand
        |> toAppResult

    let getDetail grainId =
        ReadStore.RepositoryBase.getSingle<GrainDetail> grainId readStoreGet deserialise
        |> toAppResult

    let identifyUnknownGrain getCurrentUser (req:IdentifyGrainRequest) =
        let taxonIdOrError = Converters.Identity.existingBackboneTaxonOrError readStoreGet req.TaxonId
        let grainIdOrError = Converters.Identity.existingGrainOrError readStoreGet req.GrainId
        let user = getCurrentUser() |> UserId
        let createCommand userId grainId taxonId =
            IdentifyUnknownGrain { Id = grainId; Taxon = taxonId; IdentifiedBy = userId }
        createCommand user
        <!> grainIdOrError
        <*> taxonIdOrError
        |> lift issueCommand
        |> toAppResult

    let listUnknownGrains () =
        let grainIds = RepositoryBase.getListKey<Guid> All "GrainSummary:index" readStoreGetList deserialiseGuid
        match grainIds with
        | Error e -> Error Persistence
        | Ok clist -> 
            let getCol id = ReadStore.RepositoryBase.getSingle<GrainSummary> (id.ToString()) readStoreGet deserialise
            clist 
            |> List.map getCol 
            |> List.choose (fun r -> match r with | Ok c -> Some c | Error e -> None)
            |> Ok

    let getTopScoringUnknownGrains() =
        let getCol (id:Guid) = 
            ReadStore.RepositoryBase.getSingle<GrainDetail> (id.ToString()) readStoreGet deserialise
            |> lift (fun d -> { Id = d.Id; Latitude = d.Latitude; Longitude = d.Longitude })
        RepositoryBase.getListKey<Guid> All "GrainSummary:index" readStoreGetList deserialiseGuid
        |> bind (mapResult getCol)
        |> toAppResult


module Taxonomy =

    open GlobalPollenProject.Core.Aggregates.Taxonomy

    let toSearchResult (name:string) =
        let parts = name.Split(':')
        match parts.Length with
        | 1 -> { LatinName = parts.[0]; Rank = "Family"; Heirarchy = [parts.[0]] } |> Ok
        | 2 -> { LatinName = parts.[0]; Rank = "Genus"; Heirarchy = [parts.[1]; parts.[0]] } |> Ok
        | 3 -> { LatinName = parts.[0]; Rank = "Species"; Heirarchy = [parts.[2]; parts.[1]; parts.[0]] } |> Ok
        | _ -> Error "The read model format was different to that expected"

    let private pageResponseToItems pageResponse =
        match pageResponse with
        | AllPages p -> p
        | SinglePage p -> p.Items

    let autocomplete (req:TaxonAutocompleteRequest) =
        let page = Paged {ItemsPerPage = 10; Page = 1 }
        KeyValueStore.getLexographic "Autocomplete:Taxon" req.Name page readLex
        |> lift pageResponseToItems
        |> bind (mapResult toSearchResult)
        |> toAppResult

    let private toPagedDto = function
        | AllPages p -> { Items = p
                          CurrentPage = 1
                          TotalPages = 1
                          ItemsPerPage = p |> List.length
                          ItemTotal = p |> List.length }
        | SinglePage p -> { Items = p.Items
                            CurrentPage = p.CurrentPage
                            TotalPages = p.TotalPages
                            ItemsPerPage = p.ItemsPerPage
                            ItemTotal = p.TotalItems }

    let private mapResultPagedItems f p =
        p.Items 
        |> mapResult f
        |> lift (fun i ->
            { Items = i
              CurrentPage = p.CurrentPage
              TotalPages = p.TotalPages
              ItemsPerPage = p.ItemsPerPage
              ItemTotal = p.ItemTotal } )

    let list (request:TaxonPageRequest) =
        let req = Paged {ItemsPerPage = request.PageSize; Page = request.Page }
        let key = "TaxonSummary:" + request.Rank
        let unwrapJson (Json x) : Result<string,string> = x |> Ok
        let getSummary (name:string) =
            let reversedTaxonomy = name.Split(':') |> Array.rev |> Array.fold (fun acc x -> acc + ":" + x) ""
            RepositoryBase.getKey<Guid> ("Taxon" + reversedTaxonomy) readStoreGet deserialise
            |> bind (fun x -> RepositoryBase.getKey<TaxonSummary> ("TaxonSummary:" + (x.ToString())) readStoreGet deserialise)

        KeyValueStore.getLexographic key request.Lex req readLex
        |> lift toPagedDto
        |> bind (mapResultPagedItems getSummary)
        |> toAppResult

    let private toNameSearchKey family genus species =
        match genus with
        | None -> "Taxon:" + family
        | Some g ->
            match species with
            | None -> sprintf "Taxon:%s:%s" family g
            | Some s -> 
                sprintf "Taxon:%s:%s:%s %s" family g g s

    let getByName family genus species =
        let key = toNameSearchKey family genus species
        let taxonId = RepositoryBase.getKey<Guid> key readStoreGet deserialise
        match taxonId with
        | Ok i -> 
            RepositoryBase.getKey<TaxonDetail> ("TaxonDetail:" + (i.ToString())) readStoreGet deserialise 
            |> toAppResult
        | Error _ -> Error NotFound

    let getSlide colId slideId =
        let key = sprintf "SlideDetail:%s:%s" colId slideId
        let createViewModel slide col = { Slide = slide; Collection = col }
        createViewModel
        <!> RepositoryBase.getKey<SlideDetail> key readStoreGet deserialise
        <*> RepositoryBase.getSingle<ReferenceCollectionSummary> colId readStoreGet deserialise
        |> toNotFoundResult

    let getById (taxonId:Guid) =
        let key = sprintf "TaxonDetail:%s" (taxonId.ToString())
        RepositoryBase.getKey<TaxonDetail> key readStoreGet deserialise
        |> toAppResult

module IndividualReference =

    let list (request:PageRequest) =
        let cols = RepositoryBase.getListKey<Guid> All "ReferenceCollectionSummary:index" readStoreGetList deserialiseGuid
        match cols with
        | Error e -> Error Persistence
        | Ok clist -> 
            let getCol id = ReadStore.RepositoryBase.getSingle<ReferenceCollectionSummary> (id.ToString()) readStoreGet deserialise
            clist 
            |> List.map getCol 
            |> List.choose (fun r -> match r with | Ok c -> Some c | Error e -> None)
            |> Ok

    let getDetail id version =
        let key = sprintf "ReferenceCollectionDetail:%s:V%i" id version
        RepositoryBase.getKey<ReferenceCollectionDetail> key readStoreGet deserialise
        |> toAppResult

    let getLatestVersion id =
        RepositoryBase.getKey<EditableRefCollection> id readStoreGet deserialise
        |> lift (fun c -> c.PublishedVersion)
        |> toAppResult

module Backbone =

    open GlobalPollenProject.Core.Aggregates.Taxonomy
    open ImportTaxonomy

    let issueCommand = 
        let aggregate = { initial = State.InitialState; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "Taxon" aggregate domainDependencies

    let importAll filePath =

        let processTaxa' initialCommands allTaxa taxaToProcess =
            let mutable generatedCommands : Command list = initialCommands
            let mutable results : (ParsedTaxon * Result<Command list,ImportError>) list = []
            for taxon in taxaToProcess do
                let added = createImportCommands taxon allTaxa generatedCommands domainDependencies.GenerateId
                match added with 
                | Ok cmds -> 
                    cmds |> List.map issueCommand |> ignore
                    generatedCommands <- List.append generatedCommands cmds
                | Error e ->
                    match e with
                    | Postpone -> printfn "Postponing %s" taxon.ScientificName
                    | SynonymOfSubspecies -> printfn "Synonym of Subspecies (Skipping): %s" taxon.ScientificName 
                results <- List.append results [taxon,added]
                if generatedCommands.Length % 20000 = 0 then (printfn "Commands %i" generatedCommands.Length) else ignore()
            results

        let rec processTaxa commands allTaxa taxaToProcess =
            let results = processTaxa' commands allTaxa taxaToProcess
            let toReprocess = 
                results 
                |> List.filter(fun result -> match (snd result) with | Ok r -> false | Error e -> match e with | ImportError.Postpone -> true | ImportError.SynonymOfSubspecies -> false ) 
                |> List.map fst
            let currentCommands = 
                results 
                |> List.choose(fun result -> match (snd result) with | Ok r -> Some r | Error e -> None ) 
                |> List.concat
                |> List.append commands
            printfn "Generated commands for %i taxa, with %i remaining" currentCommands.Length toReprocess.Length
            match toReprocess.Length with
            | 0 -> currentCommands
            | _ -> processTaxa currentCommands allTaxa toReprocess

        let taxa = readPlantListTextFile filePath
        let commands : Command list = processTaxa [] taxa taxa
        //printfn "Issuing %i import commands..." commands.Length
        //commands |> List.map issueCommand |> ignore
        ()

    let searchNames (request:BackboneSearchRequest) =
        request
        |> Converters.Taxonomy.backboneSearchToIdentity
        |> Result.bind (fun s -> ReadStore.TaxonomicBackbone.search s readLex All deserialise)
        |> toAppResult

    let tryMatch (request:BackboneSearchRequest) =
        request
        |> Converters.Taxonomy.backboneSearchToIdentity
        |> Result.bind (fun s -> ReadStore.TaxonomicBackbone.findMatches s readStoreGetSortedList readStoreGet deserialise)

    // Traces a backbone taxon to its most recent name (e.g. synonym -> synonym -> accepted name)
    let tryTrace (request:BackboneSearchRequest) =
        request
        |> tryMatch
        |> Result.bind (ReadStore.TaxonomicBackbone.tryTrace request.Rank request.Authorship readStoreGet deserialise)
        |> toAppResult


module User = 

    open GlobalPollenProject.Core.Aggregates.User
    
    let private issueCommand = 
        let aggregate = { initial = InitialState; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "User" aggregate domainDependencies
    
    let register (newUser:NewAppUserRequest) (getUserId:GetCurrentUser) =
        let id = UserId (getUserId())
        newUser.Organisation 
        |> ShortText.create
        |> lift (fun org -> Register { Id = id
                                       Title = newUser.Title
                                       FirstName = newUser.FirstName
                                       LastName = newUser.LastName
                                       PublicProfile = false
                                       Organisation = org } )
        |> lift issueCommand
        |> toAppResult

    let getPublicProfile (id:Guid) =
        RepositoryBase.getSingle<PublicProfile> (id.ToString()) readStoreGet deserialise
        |> toAppResult

    let grantCuration id =
        let i = Guid.TryParse id
        match fst i with
        | true ->
            snd i
            |> UserId
            |> GrantCurationRights
            |> issueCommand
            Ok id
        | false -> Error InvalidRequestFormat

module Statistic =

    let getHomeStatistics() =
        let createStatModel d s i u =
            { DigitisedSlides = d
              Species = s
              IndividualGrains = i
              UnidentifiedGrains = u }
        let getStat key = RepositoryBase.getKey<int> key readStoreGet deserialise
        createStatModel
        <!> getStat "Statistic:SlideDigitisedTotal"
        <*> getStat "Statistic:Taxon:SpeciesTotal"
        <*> getStat "Statistic:Grain:Total"
        <*> getStat "Statistic:UnknownSpecimenRemaining"
        |> toAppResult

    let getSystemStats() : Result<AllStatsViewModel,ServiceError> =

        let createViewModel famC famT genC genT speC speT =
            { Family = { Count = famC; Total = famT } 
              Genus = { Count = genC; Total = genT }
              Species = { Count = speC; Total = speT }
              TopIndividuals = []
              TopOrganisations = [] }

        let getInt key = RepositoryBase.getKey<int> key readStoreGet deserialise

        createViewModel
        <!> getInt "Statistic:Taxon:FamilyTotal"
        <*> getInt "Statistic:BackboneTaxa:Families"
        <*> getInt "Statistic:Taxon:GenusTotal"
        <*> getInt "Statistic:BackboneTaxa:Genera"
        <*> getInt "Statistic:Taxon:SpeciesTotal"
        <*> getInt "Statistic:BackboneTaxa:Species"
        |> toAppResult

module Admin =

    let rebuildReadModel() =
        inMaintainanceMode <- true
        redisReset() |> ignore
        ProjectionHandler.init redisSet () |> ignore
        eventStore.Value.ReplayDomainEvents()
        inMaintainanceMode <- false

    let listUsers() =
        let get (id:Guid) = 
            ReadStore.RepositoryBase.getSingle<PublicProfile> (id.ToString()) readStoreGet deserialise
        RepositoryBase.getListKey<Guid> All "PublicProfile:index" readStoreGetList deserialiseGuid
        |> bind (mapResult get)
        |> toAppResult

module Curation =

    open GlobalPollenProject.Core.Aggregates.ReferenceCollection

    let private issueCommand = 
        let aggregate = { initial = State.Initial; evolve = State.Evolve; handle = handle; getId = getId }
        eventStore.Value.MakeCommandHandler "ReferenceCollection" aggregate domainDependencies

    let listPending() =
        let get (id:Guid) = 
            ReadStore.RepositoryBase.getSingle<EditableRefCollection> (id.ToString()) readStoreGet deserialise
        RepositoryBase.getListKey<Guid> All "Curation:InReview" readStoreGetList deserialiseGuid
        |> bind (mapResult get)
        |> toAppResult

    let issueDecision getCurrentUser (request:CurateCollectionRequest) =
        
        let isCurator = 
            ReadStore.RepositoryBase.getSingle<PublicProfile> (getCurrentUser().ToString()) readStoreGet deserialise
            |> lift (fun u -> u.Curator)

        let decision =
            match request.Approved with
            | true -> Approved |> Ok
            | false -> 
                match request.Comment |> LongformText.create with
                | Ok t -> RevisionRequired t |> Ok
                | Error e -> Error e

        let issue isCurator decision =
            match isCurator with
            | false -> Error "You do not have curation permissions"
            | true ->
                IssuePublicationDecision (request.Collection |> CollectionId,decision,(getCurrentUser() |> UserId))
                |> issueCommand
                Ok()

        issue
        <!> isCurator
        <*> decision
        |> toAppResult


// Additional event handlers:

eventStore.Value.SaveEvent 
:> IObservable<string*obj*DateTime>
|> Observable.subscribe (EventHandlers.ExternalConnections.refresh readStoreGet Backbone.issueCommand)
|> ignore