import * as d3 from 'd3'
import * as topojson from 'topojson'
import * as noUiSlider from 'noUiSlider'

var neotomaId;
var points = [];
var yearOldest = 10000;
var yearYoungest = 1000;
var slider;
var mapElement = document.getElementById('paleo');
mapElement.style.display = 'block';
var width = $('#neotoma-map').closest('div').width();
mapElement.style.display = 'none';
var height = 250;
var svg;
var domPoints;
var projection = d3.geoEquirectangular()
                    .scale((width + 1) / 2 / Math.PI)
                    .translate([width / 2, height / 2])
                    .precision(.1);
var color = d3.scaleLinear<string>().domain([yearYoungest, yearOldest]).range(["yellow", "#83296F"]);

$('document').ready(function() {
    $("input[name=distribution]").change(function() {
        var selected = ($(this).val());
        if (selected == 'paleo') {
            $('#paleo').show();
            $('#modern').hide();
        } else {
            $('#paleo').hide();
            $('#modern').show();
        }
    });
    neotomaId = $('#NeotomaId').val();
    slider = document.getElementById('range');
    noUiSlider.create(slider, {
        start: [1, 10],
        margin: 1,
        connect: true,
        orientation: 'horizontal',
        behaviour: 'tap-drag',
        step: 1,
        range: {
            'min': 1,
            'max': 50
        },
        pips: {
            mode: 'values',
            values: [1, 5, 10, 15, 20, 30, 40, 50],
            density: 1,
            stepped: true
        }
    });
    slider.setAttribute('disabled', true);
    $('#paleo-loading').show();
    svg = d3.select('#neotoma-map').append('svg').attr('width', width).attr('height', height);
    var path = d3.geoPath().projection(projection);
    var g = svg.append("g");
    d3.json('/geojson/world110.json'), (error, topology) => {
        let geojson:any = topojson.feature(topology, topology.objects.countries)
        g.selectAll("path").data(geojson.features).enter().append("path").attr("d", path).attr('fill', 'grey')
    };
    if (neotomaId == 0) {
        $('#paleo-loading').text('Past occurrences for this taxon are not available from Neotoma.');
        $('#paleo-loading').show();
    } else {
        let points = getNeotomaPoints();
        updatePastDistributionText();
        slider.noUiSlider.on('slide', function() {
            updatePastDistributionText();
            redrawPoints();
        });
    }
});

function updatePastDistributionText() {
    var value = slider.noUiSlider.get();
    yearOldest = value[1] * 1000;
    yearYoungest = value[0] * 1000;
    $('#paleo-range-low').text(value[0]);
    $('#paleo-range-hi').text(value[1] + ' thousand');
}

function redrawPoints() {
    domPoints.attr('display', 'none');
    domPoints.filter(function(d) {
        return d.youngest < yearOldest && yearYoungest < d.oldest
    }).attr('display', '');
}

var getNeotomaPoints = function() {
    var neotomaUri = "https://api.neotomadb.org/v1/data/datasets?callback=neotomaCallback&taxonids=" + neotomaId + "&ageof=taxon&ageold=" + 50000 + "&ageyoung=" + 1000;
    $.ajax({
        url: neotomaUri,
        jsonp: false,
        jsonpCallback: 'neotomaCallback',
        cache: true,
        dataType: 'jsonp',
        error: function(xhr, textStatus, errorThrown) {
            $('#paleo-loading').text("Sorry, we couldn't establish a secure connection with NeotomaDB. Please try later.");
        }
    });
}

function neotomaCallback(result) {
    if (result.success == 0) {} else {
        points = [];
        for (var i = 0; i < result.data.length; i++) {
            var coord = {
                east: result.data[i].Site.LongitudeEast,
                north: result.data[i].Site.LatitudeNorth,
                youngest: result.data[i].AgeYoungest,
                oldest: result.data[i].AgeOldest
            };
            points.push(coord);
        }
        domPoints = svg.selectAll("circle").data(points).enter().append("circle").attr("cx", function(d) {
            return projection([d.east, d.north])[0];
        }).attr("cy", function(d) {
            return projection([d.east, d.north])[1];
        }).attr("r", "1.5px").style("opacity", 0.75).attr("fill", function(d) {
            return color(d.youngest);
        });
        $('#paleo-loading').fadeOut(1000);
        slider.removeAttribute('disabled');
    }
}