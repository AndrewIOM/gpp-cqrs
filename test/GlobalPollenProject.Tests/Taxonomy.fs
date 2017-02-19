module TaxonomyTests

open System
open Xunit
open GlobalPollenProject.Core.Types    
open GlobalPollenProject.Core.Aggregates.Taxonomy

let a = {
    initial = State.InitialState
    evolve = State.Evolve
    handle = handle
    getId = getId 
}

module ``When importing a taxon`` =

    let taxonId = TaxonId (Guid.NewGuid())
    let parentId = TaxonId (Guid.NewGuid())

    let ``A family is the highest taxonomic rank`` =
        Given a []
        |> When (Import {Id = taxonId; Name = LatinName "Apiaceae"; Rank = Family; Parent = Some parentId })
        |> ExpectInvalidArg