module GlobalPollenProject.App.Projections

open System
open System.Collections.Generic
open GlobalPollenProject.Core.DomainTypes
open GlobalPollenProject.Core.Aggregates
open GlobalPollenProject.Core.Composition
open ReadStore
open ReadModels

let readModelErrorHandler() =
    invalidOp "The read model is corrupt or out-of-sync. Rebuild now."

let inline deserialise< ^a> json = 
    let unwrap (ReadStore.Json j) = j
    Serialisation.deserialise< ^a> (unwrap json)

let serialise s = 
    let result = Serialisation.serialise s
    match result with
    | Ok r -> Ok <| ReadStore.Json r
    | Error e -> Error e

module Checkpoint =

    let init setKey =
        ReadStore.RepositoryBase.setKey 0 "Checkpoint" setKey serialise

    let getCurrentVersion getKey =
        ReadStore.RepositoryBase.getKey "Checkpoint" getKey deserialise<int>

    let increment getKey setKey () =
        let incrementCheck current = 
            ReadStore.RepositoryBase.setKey (current + 1) "Checkpoint" setKey serialise
            |> Result.bind (fun x -> Ok (current + 1))
        getCurrentVersion getKey
        |> Result.bind incrementCheck


module GrainLocation =

    let insertLocation (submitted:Grain.GrainSubmitted) =
        // Use redis geoadd on point coordinate
        // Convert domain to dto (spatial)
        // Add to redis list
        Ok()

    let handle (e:string*obj) =
        match snd e with
        | :? Grain.Event as e ->
            match e with
            | Grain.Event.GrainSubmitted s -> insertLocation s
            | _ -> Ok()
        | _ -> Ok()

module Statistics =

    // Statistic:UnknownSpecimenTotal
    // Statistic:UnknownSpecimenRemaining
    // Statistic:UnknownSpecimenIdentificationsTotal
    // Statistic:SlideTotal
    // Statistic:SlideDigitisedTotal
    // Statistic:Representation:Families:gppCount
    // Statistic:Representation:Families:backboneCount
    // Statistic:BackboneTaxa:Total

    let incrementStat key get set =
        RepositoryBase.getKey<int> key get deserialise
        |> bind (fun i -> RepositoryBase.setKey (i + 1) key set serialise)

    let decrementStat key get set =
        RepositoryBase.getKey<int> key get deserialise
        |> bind (fun i -> RepositoryBase.setKey (i - 1) key set serialise)

    let handle get getSortedList set setSortedList (e:string*obj) =
        match snd e with
        | :? Grain.Event as e ->
            match e with
            | Grain.Event.GrainSubmitted e -> 
                incrementStat "Statistic:UnknownSpecimenTotal" get set |> ignore
                incrementStat "Statistic:UnknownSpecimenRemaining" get set
            | Grain.Event.GrainIdentityConfirmed e ->
                decrementStat "Statistic:UnknownSpecimenRemaining" get set
            | Grain.Event.GrainIdentified e ->
                incrementStat "Statistic:UnknownSpecimenIdentificationsTotal" get set
            | Grain.Event.GrainIdentityUnconfirmed e ->
                incrementStat "Statistic:UnknownSpecimenRemaining" get set
            | _ -> Ok()
        | :? Taxonomy.Event as e ->
            match e with
            | Taxonomy.Event.Imported e -> incrementStat "Statistic:BackboneTaxa:Total" get set
            | _ -> Ok()
        | _ -> Ok()


module MasterReferenceCollection =

    // TaxonSummary:{Guid}              : TaxonSummary
    // TaxonSummary:{rank}:index        : Guid list
    // TaxonDetail:{Guid}               : TaxonDetail
    // Taxon:{Family}:{Genus}:{species} : Guid
    // Autocomplete:Taxon:{Rank}        : string list

    let initTaxonSummary (backboneTaxon:BackboneTaxon) : TaxonSummary =
        {
            Id = backboneTaxon.Id
            Family = backboneTaxon.Family
            Genus = backboneTaxon.Genus
            Species = backboneTaxon.Species
            LatinName = backboneTaxon.LatinName
            Authorship = backboneTaxon.NamedBy
            Rank = backboneTaxon.Rank
            SlideCount = 0
            GrainCount = 0
            ThumbnailUrl = ""
        }

    let initTaxonDetail (backboneTaxon:BackboneTaxon) parent : TaxonDetail =
        {
            Id = backboneTaxon.Id
            Family = backboneTaxon.Family
            Genus = backboneTaxon.Genus
            Species = backboneTaxon.Species
            LatinName = backboneTaxon.LatinName
            Authorship = backboneTaxon.NamedBy
            Rank = backboneTaxon.Rank
            Slides = []
            Grains = []
            Parent = parent
            Children = []
            NeotomaId = 0
            GbifId = 0
        }

    let getBackboneParent getSortedListKey getKey deserialise backboneTaxon =
        match backboneTaxon.Rank with
        | "Family" -> None |> Ok
        | "Genus" -> 
            ReadStore.TaxonomicBackbone.tryFindByLatinName backboneTaxon.Family None None getSortedListKey getKey deserialise
            |> lift (fun x -> Some { Id = x.Id ; Name = x.LatinName; Rank = "Family" })
        | "Species" ->
            ReadStore.TaxonomicBackbone.tryFindByLatinName backboneTaxon.Family (Some backboneTaxon.Genus) None getSortedListKey getKey deserialise
            |> lift (fun x -> Some { Id = x.Id ; Name = x.LatinName; Rank = "Genus" })
        | _ -> Error "Invalid taxonomic rank"

    let generateLookupValue (taxon:BackboneTaxon) =
        match taxon.Rank with 
        | "Family" -> taxon.Family
        | "Genus" -> sprintf "%s:%s" taxon.Family taxon.Genus
        | "Species" -> sprintf "%s:%s:%s" taxon.Family taxon.Genus taxon.Species
        | _ -> "Unknown"

    let updateTaxon get set setSortedList backboneId (summary:TaxonSummary) (detail:TaxonDetail) =
        let id : Guid = backboneId |> Converters.DomainToDto.unwrapTaxonId
        let bbTaxon = TaxonomicBackbone.getById backboneId get deserialise
        match bbTaxon with
        | Error e -> Error e
        | Ok t ->
            RepositoryBase.setSingle (id.ToString()) summary set serialise |> ignore
            RepositoryBase.setSortedListItem (generateLookupValue t) ("TaxonSummary:" + summary.Rank) 0. setSortedList |> ignore
            RepositoryBase.setSingle (id.ToString()) detail set serialise |> ignore
            RepositoryBase.setSortedListItem t.LatinName ("Autocomplete:Taxon:" + t.Rank) 0. setSortedList |> ignore
            let rankKey =
                match t.Rank with
                | "Family" -> sprintf "Taxon:%s" t.Family
                | "Genus" -> sprintf "Taxon:%s:%s" t.Family t.Genus
                | "Species" -> sprintf "Taxon:%s:%s:%s" t.Family t.Genus t.Species
                | _ -> invalidOp "Invalid rank"
            RepositoryBase.setKey (id.ToString()) rankKey set serialise

    let createTaxon get getSortedList set setSortedList backboneId =
        let bbTaxon = TaxonomicBackbone.getById backboneId get deserialise
        let parentNode = bbTaxon |> bind (getBackboneParent getSortedList get deserialise)
        let summary = bbTaxon |> lift initTaxonSummary
        let detail = 
            initTaxonDetail 
            <!> bbTaxon 
            <*> parentNode 
        updateTaxon get set setSortedList backboneId 
        <!> summary 
        <*> detail
        |> ignore
        summary,detail

    let getTaxon get taxonId =
        let id : Guid = taxonId |> Converters.DomainToDto.unwrapTaxonId
        RepositoryBase.getSingle<TaxonSummary> (id.ToString()) get deserialise,
        RepositoryBase.getSingle<TaxonDetail> (id.ToString()) get deserialise

    let ensureCreatedRecursive get getSortedList set setSortedList backboneId =

        let create id =
            createTaxon get getSortedList set setSortedList id

        let getHeirarchy backboneId =
            let rec traverse (ids: TaxonId list) =
                let current = TaxonomicBackbone.getById ids.Head get deserialise
                match current with
                | Ok p -> 
                    let parent = getBackboneParent getSortedList get deserialise p
                    match parent with
                    | Ok o ->
                        match o with
                        | Some p -> traverse ((p.Id |> TaxonId) :: ids)
                        | None -> ids
                    | Error e -> [] //TODO
                | Error e -> [] //TODO
            traverse [backboneId]

        let ensureMRCViewModel id =
            let summary,detail = getTaxon get id
            match summary with
            | Ok s -> summary,detail // If exists, return
            | Error e -> create id

        // TODO Ensure taxon is confirmed ONLY
        // TODO connect together view models

        let h = getHeirarchy backboneId
        let vms = h |> List.map ensureMRCViewModel
        vms |> Ok

    let publishCollection id get getSortedList set setSortedList =
        let getBackboneTaxon (id:string) = ReadStore.TaxonomicBackbone.getById (TaxonId <| Guid id) get deserialise
        let colId : Guid = id |> Converters.DomainToDto.unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) get deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            let processSlide slide =
                let taxonId = 
                    match slide.CurrentTaxonId with
                    | Some i -> Ok <| TaxonId i
                    | None -> Error "Invalid taxon ID"
                let getLatinName (s:SlideDetail) =
                    match s.Rank with
                    | "Family" -> s.CurrentFamily
                    | "Genus" -> s.CurrentGenus
                    | "Species" -> s.CurrentSpecies
                    | _ -> ""
                let slideSummaryRM = {
                    ColId       = slide.CollectionId
                    SlideId     = slide.CollectionSlideId
                    LatinName   = getLatinName slide
                    Rank        = slide.Rank
                    Thumbnail   = slide.Thumbnail
                }

                // Publish slide read model for detail pages
                let slidePublishedId = sprintf "%s:%s" (slide.CollectionId.ToString()) slide.CollectionSlideId
                RepositoryBase.setSingle slidePublishedId slide set serialise |> ignore

                // Statistics
                Statistics.incrementStat "Statistic:SlideTotal" get set |> ignore
                match slide.IsFullyDigitised with
                | true -> Statistics.incrementStat "Statistic:SlideDigitisedTotal" get set |> ignore
                | false -> ()

                let update (summaryRM:Result<TaxonSummary,string>) (detailRM:Result<TaxonDetail,string>) =
                    match detailRM with
                    | Ok d ->
                        let updatedDetail = { d with Slides = slideSummaryRM :: d.Slides }
                        match summaryRM with 
                        | Ok s ->
                            let updatedSummary = { s with SlideCount = s.SlideCount + 1; ThumbnailUrl = slideSummaryRM.Thumbnail }
                            updateTaxon get set setSortedList (d.Id |> TaxonId) updatedSummary updatedDetail 
                        | Error e -> Error e
                    | Error e -> Error e
                taxonId
                |> bind (ensureCreatedRecursive get getSortedList set setSortedList)
                |> bind (fun tlist -> tlist |> List.map (fun (s,d) -> update s d) |> Ok )
            c.Slides 
            |> List.filter (fun s -> s.IsFullyDigitised)
            |> List.map processSlide
            |> ignore
            Ok()

    let establishConnection get set id externalId =
        let getExisting (id:Guid) = RepositoryBase.getSingle<TaxonDetail> (id.ToString()) get deserialise
        let save (taxon:TaxonDetail) = RepositoryBase.setSingle (taxon.Id.ToString()) taxon set serialise
        let updateId taxon =
            match externalId with
            | Taxonomy.ThirdPartyTaxonId.NeotomaId i -> {taxon with NeotomaId = i}
            | Taxonomy.ThirdPartyTaxonId.GbifId i -> {taxon with GbifId = i}
        id
        |> Converters.DomainToDto.unwrapTaxonId
        |> getExisting
        |> lift updateId
        |> bind save

    let handle get getSortedList set setSortedList (e:string*obj) =
        match snd e with
        | :? ReferenceCollection.Event as e -> 
            match e with
            | ReferenceCollection.Event.CollectionPublished (id,date,ver) -> publishCollection id get getSortedList set setSortedList
            | _ -> Ok()
        | :? Grain.Event as e ->
            match e with
            | Grain.Event.GrainIdentityConfirmed e -> invalidOp "Help"
            | Grain.Event.GrainIdentityChanged e -> invalidOp "Help" //Get current taxon and remove grain from this taxon. Assign to new taxon.
            | Grain.Event.GrainIdentityUnconfirmed e -> invalidOp "Help" //Get current taxon and remove grain from this taxon
            | _ -> Ok()
        | :? Taxonomy.Event as e ->
            match e with
            | Taxonomy.Event.EstablishedConnection (id,exId) -> establishConnection get set id exId
            | _ -> Ok()
        | _ -> Ok()


module Grain =

    open GlobalPollenProject.Core.Aggregates.Grain

    let submit setReadModel generateThumbnail (e:GrainSubmitted) =
        let thumbUrl = 
            let result = 
                match e.Images.Head with
                | SingleImage (relUrl,cal) -> generateThumbnail relUrl
                | FocusImage (frames,s,c) ->
                    match frames |> List.length with
                    | i when i > 0 -> generateThumbnail frames.[i / 2]
                    | _ -> invalidOp "Empty focus image"
            match result with
            | Ok u -> u |> Url.unwrap
            | Error e -> ""

        let summary = { 
            Id = Converters.DomainToDto.unwrapGrainId e.Id 
            Thumbnail = thumbUrl }

        let removeUnit (x:float<_>) = float x
        let removeUnitInt (x:int<_>) = int x
        let unwrapLat (Latitude x) = x |> removeUnit
        let unwrapLon (Longitude x) = x |> removeUnit
        let lat,lon =
            match e.Spatial with
            | Site (la,lo) -> unwrapLat la, unwrapLon lo
            | _ -> 0.,0.

        let ageType,age =
            match e.Temporal with
            | None -> "",0
            | Some a ->
                match a with
                | CollectionDate calYr -> "Calendar", calYr |> removeUnitInt
                | Radiocarbon ybp -> "Radiocarbon", ybp |> removeUnitInt
                | Lead210 ybp -> "Lead210", ybp |> removeUnitInt

        let detail = {
            Id = Converters.DomainToDto.unwrapGrainId e.Id
            Images = [ ]
            FocusImages = []
            Identifications = []
            Latitude = lat
            Longitude = lon
            AgeType = ageType
            Age = age
            ConfirmedRank = ""
            ConfirmedFamily = ""
            ConfirmedGenus = ""
            ConfirmedSpecies = ""
            ConfirmedSpAuth = "" }

        ReadStore.RepositoryBase.setSingle (summary.Id.ToString()) summary setReadModel serialise |> ignore
        ReadStore.RepositoryBase.setSingle (detail.Id.ToString()) detail setReadModel serialise

    let identified get set (e:GrainIdentified) =
        let id : Guid = Converters.DomainToDto.unwrapGrainId e.Id
        let grain = ReadStore.RepositoryBase.getSingle<GrainDetail> (id.ToString()) get deserialise
        let taxon = ReadStore.TaxonomicBackbone.getById e.Taxon get deserialise

        let toHeirarchy (taxon:BackboneTaxon) =
            taxon.Family,taxon.Genus,taxon.Species,taxon.NamedBy,taxon.Rank

        let idMethod = "Morphological"

        let createIdentification userId idMethod heirarchy =
            let family,genus,species,namedBy,rank = heirarchy
            { User = userId |> Converters.DomainToDto.unwrapUserId 
              IdentificationMethod = idMethod
              Rank = rank
              Family = family
              Genus = genus
              Species = species
              SpAuth = namedBy }

        let identification =
            createIdentification e.IdentifiedBy idMethod
            <!> (taxon |> lift toHeirarchy)

        let addId id grain = { grain with Identifications = id :: grain.Identifications }
        let save grain = ReadStore.RepositoryBase.setSingle (id.ToString()) grain set serialise

        addId
        <!> identification
        <*> grain
        |> save

    let identityChanged get set (taxon:TaxonId option) grainId = 
        let id : Guid = Converters.DomainToDto.unwrapGrainId grainId
        let grain = ReadStore.RepositoryBase.getSingle<GrainDetail> (id.ToString()) get deserialise
        let save grain = ReadStore.RepositoryBase.setSingle (id.ToString()) grain set serialise

        match taxon with
        | Some t ->
            let taxon = ReadStore.TaxonomicBackbone.getById t get deserialise
            let toHeirarchy (taxon:BackboneTaxon) =
                taxon.Family,taxon.Genus,taxon.Species,taxon.NamedBy,taxon.Rank
            let switchCurrentTaxon heirarchy grain =
                let family,genus,species,namedBy,rank = heirarchy
                { grain with ConfirmedFamily = family
                             ConfirmedGenus = genus
                             ConfirmedSpecies = species
                             ConfirmedSpAuth = namedBy
                             ConfirmedRank = rank }
            switchCurrentTaxon
            <!> (taxon |> lift toHeirarchy)
            <*> grain
            |> save
        
        | None ->
            let update grain = { grain with ConfirmedFamily = ""
                                            ConfirmedGenus = ""
                                            ConfirmedSpecies = ""
                                            ConfirmedSpAuth = ""
                                            ConfirmedRank = "" }
            grain
            |> lift update
            |> bind save


    let handle get set generateThumb (e:string*obj) =
        match snd e with
        | :? Grain.Event as e ->
            match e with
            | Grain.Event.GrainSubmitted e -> submit set generateThumb e
            | Grain.Event.GrainIdentified e -> identified get set e 
            | Grain.Event.GrainIdentityChanged e -> identityChanged get set (Some e.Taxon) e.Id
            | Grain.Event.GrainIdentityConfirmed e -> identityChanged get set (Some e.Taxon) e.Id
            | Grain.Event.GrainIdentityUnconfirmed e -> identityChanged get set None e.Id
        | _ -> Ok()


module TaxonomicBackbone =

    open GlobalPollenProject.Core.Aggregates.Taxonomy

    let getById getKey id =
        match id with
        | Some id ->
            let u : Guid = Converters.DomainToDto.unwrapTaxonId id
            match ReadStore.RepositoryBase.getSingle<BackboneTaxon> (u.ToString()) getKey deserialise with
            | Ok t -> t
            | Error e -> readModelErrorHandler()
        | None -> readModelErrorHandler()

    let addToBackbone getKey getSortedListKey setKey setSortedList serialise deserialise (event:Imported) =

        let getFamily familyName =
            ReadStore.TaxonomicBackbone.tryFindByLatinName familyName None None getSortedListKey getKey deserialise

        let reference, referenceUrl =
            match event.Reference with
            | None -> "", ""
            | Some r -> 
                match r with
                | ref,Some u -> ref,Url.unwrap u
                | ref,None -> ref,""

        let family,genus,species,rank,ln,namedBy,fId,gId,sId =
            match event.Identity with
            | Family ln -> 
                let fId = Converters.DomainToDto.unwrapTaxonId event.Id
                Converters.DomainToDto.unwrapLatin ln,"","", "Family", Converters.DomainToDto.unwrapLatin ln,"",fId,Nullable<Guid>(),Nullable<Guid>()
            | Genus ln ->
                let family = getById getKey event.Parent
                let fId = family.Id
                let gId = Converters.DomainToDto.unwrapTaxonId event.Id
                family.LatinName,Converters.DomainToDto.unwrapLatin ln,"", "Genus", Converters.DomainToDto.unwrapLatin ln,"",fId,Nullable<Guid>(gId),Nullable<Guid>()
            | Species (g,s,n) -> 
                let species = sprintf "%s %s" (Converters.DomainToDto.unwrapLatin g) (Converters.DomainToDto.unwrapEph s)
                let genus = getById getKey event.Parent
                let family = getFamily genus.Family
                match family with
                | Ok f ->
                    let fId = f.Id
                    let gId = genus.Id
                    let sId = Converters.DomainToDto.unwrapTaxonId event.Id
                    f.LatinName, genus.LatinName, species,"Species", species,Converters.DomainToDto.unwrapAuthor n,fId,Nullable<Guid>(gId),Nullable<Guid>(sId)
                | Error e -> readModelErrorHandler()

        let status,alias =
            match event.Status with
            | Accepted -> "accepted",""
            | Doubtful -> "doubtful",""
            | Misapplied id -> "misapplied",(id |> Converters.DomainToDto.unwrapTaxonId).ToString()
            | Synonym id -> "synonym",(id |> Converters.DomainToDto.unwrapTaxonId).ToString()

        let group =
            match event.Group with
            | TaxonomicGroup.Angiosperm -> "Angiosperm"
            | TaxonomicGroup.Bryophyte -> "Bryophyte"
            | TaxonomicGroup.Gymnosperm -> "Gymnosperm"
            | TaxonomicGroup.Pteridophyte -> "Pteridophyte"

        let projection = 
            {   Id = Converters.DomainToDto.unwrapTaxonId event.Id
                Group = group
                Family = family
                Genus = genus
                Species = species
                FamilyId = fId
                GenusId = gId
                SpeciesId = sId
                LatinName = ln
                NamedBy = namedBy
                TaxonomicStatus = status
                TaxonomicAlias = alias
                Rank = rank
                ReferenceName = reference
                ReferenceUrl = referenceUrl }
        ReadStore.TaxonomicBackbone.import setKey setSortedList serialise projection

    let handle get getSortedList set setSortedList (e:string*obj) =
        match snd e with
        | :? Taxonomy.Event as e -> 
            match e with
            | Taxonomy.Event.Imported t -> addToBackbone get getSortedList set setSortedList serialise deserialise t
            | _ -> Ok()
        | _ -> Ok()


module ReferenceCollectionReadOnly =

    // ReferenceCollectionSummary:{Guid}            : ReferenceCollectionSummary
    // ReferenceCollectionDetail:{Guid}:V{Version}  : ReferenceCollectionDetail

    let published get set setList (colId:CollectionId) time version =
        let id : Guid = colId |> Converters.DomainToDto.unwrapRefId
        let col = RepositoryBase.getSingle (id.ToString()) get deserialise<EditableRefCollection>
        match col with
        | Ok c ->
            let summary = {
                Id              = c.Id
                Name            = c.Name
                Description     = c.Description
                SlideCount      = c.SlideCount
                Published       = time
                Version         = Converters.DomainToDto.unwrapColVer version
            }
            let v = Converters.DomainToDto.unwrapColVer version
            let detail = {
                Id              = c.Id
                Name            = c.Name
                Description     = c.Description
                Published       = time
                Version         = v
                Slides          = c.Slides
                Contributors    = []
            }
            RepositoryBase.setSingle (id.ToString()) summary set serialise |> ignore
            RepositoryBase.setKey detail (sprintf "ReferenceCollectionDetail:%s:V%i" (id.ToString()) v) set serialise |> ignore
            RepositoryBase.setListItem (id.ToString()) "ReferenceCollectionSummary:index" setList |> ignore
            Ok()
        | Error e -> Error e

    let handle get set setList (e:string*obj) =
        match snd e with
        | :? ReferenceCollection.Event as e ->
            match e with
            | ReferenceCollection.Event.CollectionPublished (id,time,version) -> published get set setList id time version
            | _ -> Ok()
        | _ -> Ok()


module UserProfile =

    let registered user =
        Ok()

    let handle (e:string*obj) =
        match snd e with
        | :? User.Event as e ->
            match e with
            | User.Event.JoinedClub (x,y) -> invalidOp "Cool"
            | User.Event.ProfileHidden x -> invalidOp "Cool"
            | User.Event.ProfileMadePublic x -> invalidOp "Cool"
            | User.Event.UserRegistered x -> registered x
        | _ -> Ok()


module Digitisation =

    // CollectionDrafts:{ColId}         : EditableCollection
    // CollectionAccessList:{UserId}    : ColId list

    open GlobalPollenProject.Core.Aggregates.ReferenceCollection
    open Converters.DomainToDto

    let started (set:SetStoreValue) (setList:SetEntryInList) (e:DigitisationStarted) =
        let col = {
            Id = e.Id |> unwrapRefId
            Name = e.Name
            Description = e.Description
            EditUserIds = [ e.Owner |> unwrapUserId ]
            LastEdited = DateTime.Now //TODO remove to parameter function
            PublishedVersion = 0
            SlideCount = 0
            Slides = [] }
        let id : Guid = e.Id |> unwrapRefId
        let userId : Guid = e.Owner |> unwrapUserId
        RepositoryBase.setSingle (id.ToString()) col set serialise |> ignore
        RepositoryBase.setListItem (id.ToString()) ("CollectionAccessList:" + (userId.ToString())) setList

    let recordSlide getKey setKey (e:SlideRecorded) =
        let colId : Guid = e.Id |> unwrapSlideId |> fst |> unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) getKey deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            let rank =
                if String.IsNullOrEmpty e.OriginalGenus then "Family"
                else if String.IsNullOrEmpty e.OriginalSpecies then "Genus"
                else "Species" 
            let slide = {
                CollectionId = e.Id |> unwrapSlideId |> fst |> unwrapRefId
                CollectionSlideId = e.Id |> unwrapSlideId |> snd
                FamilyOriginal = e.OriginalFamily
                GenusOriginal = e.OriginalGenus
                SpeciesOriginal = e.OriginalSpecies
                Rank = rank
                CurrentTaxonId = None
                CurrentFamily = ""
                CurrentGenus = ""
                CurrentTaxonStatus = ""
                CurrentSpecies = ""
                CurrentSpAuth = ""
                IsFullyDigitised = false
                Thumbnail = ""
                Images = [] }
            RepositoryBase.setSingle (colId.ToString()) { c with Slides = slide::c.Slides; SlideCount = c.SlideCount + 1 } setKey serialise

    let imageUploaded getKey setKey generateThumbnail toAbsoluteUrl id image =
        let colId : Guid = id |> unwrapSlideId |> fst |> unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) getKey deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            let slide = c.Slides |> List.tryFind (fun x -> x.CollectionSlideId = (id |> unwrapSlideId |> snd))
            match slide with
            | None -> readModelErrorHandler()
            | Some s ->
                let thumbnailUrl = 
                    let result = 
                        match image with
                        | SingleImage (relUrl,cal) -> generateThumbnail relUrl
                        | FocusImage (frames,s,c) ->
                            match frames |> List.length with
                            | i when i > 0 -> generateThumbnail frames.[i / 2]
                            | _ -> invalidOp "Empty focus image"
                    match result with
                    | Ok u -> u |> Url.unwrap
                    | Error e -> ""
                let getMag a = None //TODO enable fetching of magnification calibrations
                let imageDto = Converters.DomainToDto.image getMag toAbsoluteUrl image
                let updatedSlide = { s with Images = imageDto :: s.Images; Thumbnail = thumbnailUrl }
                let updatedSlides = 
                    c.Slides 
                    |> List.map (fun x -> if x.CollectionSlideId = s.CollectionSlideId then updatedSlide else x)
                    |> List.sortBy (fun s -> s.CollectionSlideId)
                let updatedCol = { c with Slides = updatedSlides }
                RepositoryBase.setSingle (colId.ToString()) updatedCol setKey serialise

    let digitised getKey setKey id =
        let colId : Guid = id |> unwrapSlideId |> fst |> unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) getKey deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            let slide = c.Slides |> List.tryFind (fun x -> x.CollectionSlideId = (id |> unwrapSlideId |> snd))
            match slide with
            | None -> readModelErrorHandler()
            | Some s ->
                let updatedSlide = { s with IsFullyDigitised = true }
                let updatedSlides = c.Slides |> List.map (fun x -> if x.CollectionSlideId = s.CollectionSlideId then updatedSlide else x)
                let updatedCol = { c with Slides = updatedSlides }
                RepositoryBase.setSingle (colId.ToString()) updatedCol setKey serialise

    let gainedIdentity getKey setKey id taxonId =
        let colId : Guid = id |> unwrapSlideId |> fst |> unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) getKey deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            let slide = c.Slides |> List.tryFind (fun x -> x.CollectionSlideId = (id |> unwrapSlideId |> snd))
            match slide with
            | None -> readModelErrorHandler()
            | Some s ->
                let f,g,sp,auth,status = 
                    let bbTaxon = RepositoryBase.getSingle ((taxonId |> unwrapTaxonId).ToString()) getKey deserialise<BackboneTaxon>
                    match bbTaxon with
                    | Error e -> readModelErrorHandler()
                    | Ok t -> t.Family, t.Genus, t.Species, t.NamedBy, t.TaxonomicStatus
                let updatedSlide = { s with CurrentTaxonStatus = status; CurrentFamily = f; CurrentGenus = g; CurrentSpecies = sp; CurrentSpAuth = auth; CurrentTaxonId = taxonId |> Converters.DomainToDto.unwrapTaxonId |> Some }
                let updatedSlides = c.Slides |> List.map (fun x -> if x.CollectionSlideId = s.CollectionSlideId then updatedSlide else x)
                let updatedCol = { c with Slides = updatedSlides }
                RepositoryBase.setSingle (colId.ToString()) updatedCol setKey serialise

    let published getKey setKey id time (version:ColVersion) =
        let colId : Guid = id |> unwrapRefId
        let col = RepositoryBase.getSingle (colId.ToString()) getKey deserialise<EditableRefCollection>
        match col with
        | Error e -> Error e
        | Ok c -> 
            RepositoryBase.setSingle (colId.ToString()) { c with PublishedVersion = ColVersion.unwrap version; LastEdited = time } setKey serialise

    let handle get getSortedList set setList generateThumb toAbsoluteUrl (e:string*obj) =
        match snd e with
        | :? ReferenceCollection.Event as e ->
            match e with
            | ReferenceCollection.Event.DigitisationStarted e -> started set setList e
            | ReferenceCollection.Event.SlideRecorded e -> recordSlide get set e
            | ReferenceCollection.Event.SlideImageUploaded (s,i,y) -> imageUploaded get set generateThumb toAbsoluteUrl s i
            | ReferenceCollection.Event.SlideFullyDigitised e -> digitised get set e
            | ReferenceCollection.Event.SlideGainedIdentity (s,t) -> gainedIdentity get set s t
            | ReferenceCollection.Event.CollectionPublished (id,d,v) -> published get set id d v
        | _ -> Ok()


module Calibration =

    open GlobalPollenProject.Core.Aggregates.Calibration
    open Converters.DomainToDto

    // Calibration:User:{UserId}   : CalId list
    // Calibration:{CalId}         : Calibration

    let removeUnitInt (x:int<_>) = int x
    let removeUnitFloat (x:float<_>) = float x

    let setup set setList (e:SetupMicroscope) =
        let cal = Converters.DomainToDto.calibration e.Id e.User e.FriendlyName e.Microscope
        let id = (e.Id |> unwrapCalId).ToString()
        RepositoryBase.setSingle id cal set serialise |> ignore
        RepositoryBase.setListItem id ("Calibration:User:" + ((e.User |> unwrapUserId).ToString())) setList

    let calibrated get set toAbsoluteUrl e =
        let calId : Guid = e.Id |> unwrapCalId
        let cal = RepositoryBase.getSingle (calId.ToString()) get deserialise<Calibration> 
        match cal with
        | Error e -> Error e
        | Ok c ->
            let newMag = {
                Level = e.Magnification |> removeUnitInt
                Image = e.Image |> toAbsoluteUrl |> Url.unwrap
                PixelWidth = e.PixelWidth |> removeUnitFloat
            }
            RepositoryBase.setSingle (calId.ToString()) { c with Magnifications = newMag :: c.Magnifications } set serialise

    let handle get getList set setList toAbsoluteUrl (e:string*obj) =
        match snd e with
        | :? Event as e ->
            match e with
            | Event.SetupMicroscope e -> setup set setList e
            | Event.CalibratedMag e -> calibrated get set toAbsoluteUrl e
        | _ -> Ok()
