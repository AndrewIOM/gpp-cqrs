$('document').ready(function () {
    
        var width = $('#locations-map').width();
        var height = width * 0.5;
    
        var projection = d3.geo.equirectangular()
            .scale((width + 1) / 2 / Math.PI)
            .translate([width / 2, height * 0.5])
            .precision(.1);
    
        var svg = d3.select("#locations-map").append("svg")
            .attr("width", width)
            .attr("height", height);
        var path = d3.geo.path()
            .projection(projection);
        var g = svg.append("g").attr('class','map');
    
        d3.json('/geojson/world110.json', function (error, topology) {
            g.selectAll("path")
                .data(topojson.object(topology, topology.objects.countries)
                    .geometries)
                .enter()
                .append("path")
                .attr("d", path);
        });
    
        d3.json("/api/v1/grain/location", function (error, data) {
            var unidentifiedNumber = document.getElementById('unidentified-count');
            unidentifiedNumber.innerHTML = '0';
            var unidentifiedCounter = 0;
    
            var circles = svg.selectAll("circle")
                .data(data).enter()
                .append("svg:a")
                .attr("xlink:href", function (d) { return '/Identify/' + d.Id; })
                .append("circle")
                .attr('opacity', 0)
                .attr("cx", function (d) { return projection([d.Longitude, d.Latitude])[0]; })
                .attr("cy", function (d) { return projection([d.Longitude, d.Latitude])[1]; })
                .attr("r", 4)
                .attr('stroke', '#83296F')
                .attr('stroke-width', 2)
                .attr("fill", 'rgba(184, 0, 136, 0.1)')
                .on('mouseenter', function () {
                    d3.select(this)
                      .transition()
                      .attr('r', 8);
                })
           .on('mouseleave', function () {
               d3.select(this)
                 .transition()
                 .attr('r', 4);
           })
                .transition()
                .duration(500) 
                .each("start", function () { 
                    unidentifiedCounter++;
                    unidentifiedNumber.innerHTML = unidentifiedCounter;
                })
                .delay(function (d, i) {
                    return 2000 + (i / data.length * 1500);
                })
                .ease("variable")
                .attr('opacity', '1');
        });
    });