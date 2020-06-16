module GlobalPollenProject.Web.App

open System
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication
open FSharp.Control.Tasks.V2.ContextInsensitive
open Giraffe
open ReadModels
open Handlers
open Connections

// TODO Move this into the error types from core API
let inMaintenanceMode = false

let maintenanceResult ctx =
    ctx |> (clearResponse >=> setStatusCode 503 >=> htmlView HtmlViews.StatusPages.maintainance)

// TODO Move function into error handler
let notInMaintenanceMode next ctx : HttpFuncResult =
    match inMaintenanceMode with
    | true -> maintenanceResult next ctx
    | false -> next ctx

let accessDenied = setStatusCode 401 >=> htmlView HtmlViews.StatusPages.denied
let notFound ctx = ctx |> (clearResponse >=> setStatusCode 400 >=> htmlView HtmlViews.StatusPages.notFound)
let mustBeLoggedIn : HttpHandler = requiresAuthentication (redirectTo false Urls.Account.login)
let mustBeAdmin ctx = requiresRole "Admin" accessDenied ctx


/////////////////////////
/// Route HTTP Handlers
/////////////////////////

module Actions =
    
    let login : HttpHandler =
        requiresAuthentication (challenge "OpenIdConnect") >=>
        fun next ctx ->
            let user = ctx.User
            let token = ctx.GetTokenAsync("access_token") |> Async.AwaitTask |> Async.RunSynchronously
            ctx.Items.Add("access_token",token)
            redirectTo true "/" next ctx

    let logout = signOut "Cookies" >=> redirectTo false "/"
    
    module Docs =

        open Docs
        
        /// Index of markdown files included in Docs folder
        let docIndex : HttpHandler =
            fun next ctx ->
                guideDocuments 
                |> Array.map (fun (meta,html) -> {Html = html; Metadata = meta |> dict; Headings = getSidebarHeadings html}) 
                |> Seq.toList
                |> HtmlViews.Guide.contentsView
                |> renderView next ctx

        let docSection docSection =
            fun next ctx ->
                let r = guideDocuments |> Array.tryFind (fun (n,_) -> n |> List.find (fun (k,_) -> k = "ShortTitle") |> snd = docSection)
                match r with
                | Some (meta,html) -> 
                    {Html = html; Metadata = meta |> dict; Headings = getSidebarHeadings html} 
                    |> HtmlViews.Guide.sectionView
                    |> renderView next ctx
                | None -> notFound next ctx

    module MasterCollection =

        let defaultIfNull (req:TaxonPageRequest) =
            match String.IsNullOrEmpty req.Rank with
            | true -> { Page = 1; PageSize = 50; Rank = "Genus"; Lex = "" }
            | false ->
                if req.PageSize = 0 then { req with PageSize = 50}
                else req

        let pagedTaxonomy : HttpHandler =
            fun next ctx ->
                task {
                    return!
                        ctx.BindQueryString<TaxonPageRequest>()
                        |> defaultIfNull
                        |> CoreActions.MRC.list
                        |> fun act -> coreAction act HtmlViews.MRC.index next ctx
                }
    
        let slideView (id:string) : HttpHandler =
            fun next ctx ->
                let core = ctx.GetService<CoreMicroservice>()
                let split = id.Split '/'
                match split.Length with
                | 2 -> 
                    let col,slide = split.[0], split.[1] |> Net.WebUtility.UrlDecode
                    CoreActions.MRC.getSlide col slide
                    |> core.Apply
                    |> Async.RunSynchronously // TODO Remove!
                    |> renderViewResult HtmlViews.ReferenceCollections.slideView next ctx
                | 3 ->
                    let col,slide = split.[0], split.[2] |> Net.WebUtility.UrlDecode
                    CoreActions.MRC.getSlide col slide
                    |> core.Apply
                    |> Async.RunSynchronously // TODO Remove!
                    |> renderViewResult HtmlViews.ReferenceCollections.slideView next ctx
                | _ -> notFound next ctx

        let taxonDetail (taxon:string) : HttpHandler =
            fun next ctx ->
                let core = ctx.GetService<CoreMicroservice>()
                let (f,g,s) =
                    let split = taxon.Split '/'
                    match split.Length with
                    | 1 -> split.[0],None,None
                    | 2 -> split.[0],Some split.[1],None
                    | 3 -> split.[0],Some split.[1],Some split.[2]
                    | _ -> "",None,None
                CoreActions.MRC.getByName f "" "" //g s //TODO Fix this
                |> core.Apply
                |> Async.RunSynchronously
                |> renderViewResult HtmlViews.Taxon.view next ctx

        let taxonDetailLegacyId id =
            let old = LegacyTaxonomy.taxonLookup |> Seq.tryFind(fun t -> t.OriginalId = id)
            match old with
            | Some t ->
                match t.Rank with
                | "Family" -> redirectTo true (sprintf "/Taxon/%s" t.Family)
                | "Genus" -> redirectTo true (sprintf "/Taxon/%s/%s" t.Family t.Genus)
                | "Species" -> redirectTo true (sprintf "/Taxon/%s/%s/%s" t.Family t.Genus t.Species)
                | _ -> notFound
            | None -> notFound
        
        let taxonDetailById (id:string) : HttpHandler =
            fun next ctx ->
                match Guid.TryParse id with
                | (true,g) ->
                    task {
                        return! coreAction (CoreActions.MRC.getById g) HtmlViews.Taxon.view next ctx
                    }
                | (false,_) -> notFound next ctx

    module Collection =

        let individualCollectionIndex =
            coreAction (CoreActions.IndividualCollections.list {Page = 1; PageSize = 20}) HtmlViews.ReferenceCollections.tableView

        /// Display the contents of a specific version of a reference collection 
        let individualCollection (colId:string) version =
            coreAction (CoreActions.IndividualCollections.collectionDetail colId version) HtmlViews.ReferenceCollections.tableView

        /// Display the latest version of a specific reference collection
        let individualCollectionLatest (colId:string) next (ctx:HttpContext) =
            let core = ctx.GetService<CoreMicroservice>()
            let latestVer = core.Apply(CoreActions.IndividualCollections.collectionDetailLatest colId) |> Async.RunSynchronously
            match latestVer with
            | Ok v -> redirectTo false (sprintf "/Reference/%s/%i" colId v) next ctx
            | Error _ -> notFound next ctx

    module Identify =

         /// List the most sought identifications
        let topUnknownGrains next (ctx:HttpContext) =
            let core = ctx.GetService<CoreMicroservice>()
            core.Apply(CoreActions.UnknownMaterial.mostWanted()) 
            |> Async.RunSynchronously
            |> toApiResult next ctx

        let listGrains = coreAction (CoreActions.UnknownMaterial.list()) HtmlViews.Identify.index

        let showGrainDetail id = coreAction (CoreActions.UnknownMaterial.itemDetail id) HtmlViews.Identify.view

        let submitGrain next (ctx:HttpContext) =
             tryBindJson<AddUnknownGrainRequest> ctx
             |> Result.map(CoreActions.UnknownMaterial.submit >> coreAction)
             |> toApiResult next ctx

        let submitIdentification : HttpHandler =
            fun next ctx ->
            task {
                let! model = ctx.BindFormAsync<IdentifyGrainRequest>()
                let! result = coreAction' (CoreActions.UnknownMaterial.identify model) ctx
                return! redirectTo true (sprintf "/Identify/%A" model.GrainId) next ctx
            }

    module Admin =

        let rebuildReadModel next (ctx:HttpContext) =
            let core = ctx.GetService<CoreMicroservice>()
            core.Apply(CoreActions.System.rebuildReadModel ()) 
            |> Async.RunSynchronously
            |> toApiResult next ctx

        let userAdmin next ctx =
            accessDenied next ctx
            // TODO Hook up admin function
            //Admin.listUsers()
            //|> renderViewResult HtmlViews.Admin.users next ctx
    
    module Stats =

        let systemStats = coreAction(CoreActions.Statistics.system()) HtmlViews.Statistics.view


/////////////////////////
/// Routes
/////////////////////////

let webApp : HttpHandler = 

    let account =
        GET >=> choose [
            route  Urls.Account.login                >=> Actions.login
            route  Urls.Account.register             >=> Actions.login
            route  Urls.Account.logout               >=> Actions.logout
        ]

    let api =
        GET >=> choose [
            route   "/backbone/match"           >=> apiResultFromQuery CoreActions.Backbone.tryMatch
            route   "/backbone/trace"           >=> apiResultFromQuery<BackboneSearchRequest,BackboneTaxon list> CoreActions.Backbone.tryTrace
            route   "/backbone/search"          >=> apiResultFromQuery<BackboneSearchRequest,string list> CoreActions.Backbone.search
            route   "/taxon/search"             >=> apiResultFromQuery<TaxonAutocompleteRequest,TaxonAutocompleteItem list> CoreActions.MRC.autocompleteTaxon
            route   "/grain/location"           >=> Actions.Identify.topUnknownGrains
        ]

    let masterReferenceCollection =
        GET >=> 
        choose [   
            route   Urls.MasterReference.root   >=> Actions.MasterCollection.pagedTaxonomy
            routef  "/Taxon/View/%i"            Actions.MasterCollection.taxonDetailLegacyId
            routef  "/Taxon/ID/%s"              Actions.MasterCollection.taxonDetailById
            routef  "/Taxon/%s"                 Actions.MasterCollection.taxonDetail
        ]

    let individualRefCollections =
        GET >=> choose [
            route   ""                          >=> Actions.Collection.individualCollectionIndex
            routef   "/Grain/%i"                (fun _ -> setStatusCode 404 >=> htmlView HtmlViews.StatusPages.notFound)
            routef  "/%s/%i"                    (fun (id,v) -> Actions.Collection.individualCollection id v)
            routef  "/%s"                       Actions.MasterCollection.slideView
        ]

    let identify =
        choose [
            POST >=> route  "/Upload"           >=> mustBeLoggedIn >=> Actions.Identify.submitGrain
            POST >=> route  "/Identify"         >=> Actions.Identify.submitIdentification
            GET  >=> route  ""                  >=> Actions.Identify.listGrains
            GET  >=> route  "/Upload"           >=> mustBeLoggedIn >=> htmlView (HtmlViews.Identify.add 0.)
            GET  >=> routef "/%s"               Actions.Identify.showGrainDetail
        ]

    let admin =
        choose [
            GET  >=> route Urls.Admin.users                >=> mustBeAdmin >=> Actions.Admin.userAdmin
            GET  >=> route Urls.Admin.rebuildReadModel     >=> mustBeAdmin >=> Actions.Admin.rebuildReadModel
        ]

    choose [
        subRoute            "/api/v1"                   api
        routeStartsWith     Urls.Account.root           >=> account
        routeStartsWith     Urls.MasterReference.root   >=> masterReferenceCollection
        subRoute            Urls.Collections.root       individualRefCollections
        subRoute            Urls.Identify.root          identify
        subRoute            Urls.Admin.root             admin
        GET >=> choose [
            route   Urls.home                   >=> coreAction (CoreActions.Statistics.home()) HtmlViews.Home.view
            route   Urls.guide                  >=> Actions.Docs.docIndex
            routef  "/Guide/%s"                 Actions.Docs.docSection
            route   Urls.statistics             >=> Actions.Stats.systemStats
            route   Urls.api                    >=> Actions.Docs.docSection "API"
            route   Urls.tools                  >=> htmlView HtmlViews.Tools.main
            route   Urls.cite                   >=> Actions.Docs.docSection "Cite"
            route   Urls.terms                  >=> Actions.Docs.docSection "Terms"
        ]
        setStatusCode 404 >=> htmlView HtmlViews.StatusPages.notFound
    ]
