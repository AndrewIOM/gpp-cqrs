[<AutoOpen>]
module GlobalPollenProject.Core.Types

open System

type LogMessage =
| Error of string
| Info of string

type RootAggregateId = System.Guid

// Identities
type UserId = UserId of RootAggregateId
type ClubId = ClubId of RootAggregateId
type CalibrationId = CalibrationId of RootAggregateId
type CollectionId = CollectionId of RootAggregateId
type SlideId = SlideId of CollectionId * string
type GrainId = GrainId of RootAggregateId
type TaxonId = TaxonId of RootAggregateId

// Specialist Types
//type Url = Url of string

[<AutoOpen>]
module Url =
    type Url = private Url of string
    let create surl =
        Url surl
    let unwrap (Url u) = u

// Images
[<Measure>] type um
type Base64Image = Base64Image of string

type ImageForUpload =
    | Focus of Base64Image list * Stepping * CalibrationId
    | Single of Base64Image
and Image = 
    | FocusImage of Url list * Stepping * CalibrationId
    | SingleImage of Url

and Stepping =
| Fixed of float<um>
| Variable

// Sample Collection (Space + Time)
[<Measure>]
type DD

[<Measure>]
type CalYr

[<Measure>]
type YBP

type Latitude = Latitude of float<DD>
type Longitude = Longitude of float<DD>
type Point = Latitude * Longitude
type Polygon = Point list

type Site = Latitude * Longitude

type Continent = 
    | Asia
    | America
    | Europe

type SamplingLocation =
    | Site of Point
    | Region of Polygon
    | Country of string // Country name
    | Continent of Continent

type Age =
    | CollectionDate of int<CalYr>
    | Radiocarbon of int<YBP>
    | Lead210 of int<YBP>

// Sample Preperation
type ChemicalTreatments =
    | HF
    | No

type MountingMedium =
    | Glycerol


type SampleType =
    | Palaeopalynology 
    | Melissopalynology
    | Aeropalynology
    | PollinationBiology
    | ReferenceMaterial
    | Forensic

type PalaeoSiteType =
    | Lake

// Taxonomy
type LatinName = LatinName of string
type SpecificEphitet = SpecificEphitet of string
type Authorship = Scientific of string

type TaxonomicGroup =
| Angiosperm
| Gymnosperm
| Pteridophyte
| Bryophyte

type TaxonomicIdentity =
| Family of LatinName
| Genus of LatinName
| Species of LatinName * SpecificEphitet * Authorship

type TaxonomicStatus =
| Accepted
| Doubtful
| Misapplied of TaxonId
| Synonym of TaxonId

// Taxonomic Identity
type TaxonIdentification =
    | Botanical of TaxonId
    | Environmental of TaxonId
    | Morphological of TaxonId

type IdentificationStatus =
    | Unidentified
    | Partial of TaxonIdentification list
    | Confirmed of TaxonIdentification list * TaxonId

// Pollen Traits
type GrainDiameter = float<um>
type WallThickness = float<um>

type GrainShape =
    | Bisacchate
    | Circular
    | Ovular
    | Triangular
    | Trilobate
    | Pentagon
    | Hexagon
    | Unsure

type Patterning =
    | Patterned
    | Clean
    | Unsure

type Pores =
    | Pore
    | Furrow
    | PoreAndFurrow
    | No
    | Unsure

// Taxonomic Backbone
type BackboneQuery =
| Validate of TaxonomicIdentity
| ValidateById of TaxonId

type LinkRequest = {Family:string;Genus:string option;Species:string option;Identity:TaxonomicIdentity}

// Infrastructure
type Dependencies = 
    {GenerateId:        unit -> Guid; 
     Log:               LogMessage -> unit
     UploadImage:       ImageForUpload -> Image
     ValidateTaxon:     BackboneQuery -> TaxonId option
     GetGbifId:         LinkRequest -> int option
     GetNeotomaId:      LinkRequest -> int option
     CalculateIdentity: TaxonIdentification list -> TaxonId option }

type RootAggregate<'TState, 'TCommand, 'TEvent> = {
    initial:    'TState
    evolve:     'TState -> 'TEvent -> 'TState
    handle:     Dependencies -> 'TCommand -> 'TState -> 'TEvent list
    getId:      'TCommand -> RootAggregateId }