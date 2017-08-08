
////////////////////////
/// Setup - KO Bindings
////////////////////////

ko.bindingHandlers.BSModal= {
    init: function (element, valueAccessor) {
        var value = valueAccessor();
        $(element).modal({ keyboard: false, show: ko.unwrap(value) });
    },
    update: function (element, valueAccessor) {
         var value = valueAccessor();
         console.log(value);
         ko.unwrap(value) ? $(element).modal('show') : $(element).modal('hide');
    }
};

////////////////////////
/// Root View Model
////////////////////////

$(document).ready(function() {
  var vm = new DigitiseViewModel();
  vm.switchView(CurrentView.MASTER);
  ko.applyBindings(vm);
});

var CurrentView = {
  MASTER: 1,
  DETAIL: 2,
  ADD_COLLECTION: 3,
  ADD_SLIDE_RECORD: 4,
  SLIDE_DETAIL: 5,
  CALIBRATE: 6
};

function DigitiseViewModel(users, analyses) {
    var self = this;
    let apiPrefix = "/api/v1/digitise/";
    self.currentView = ko.observable(CurrentView.BASE);
    self.myCollections = ko.observableArray([]);
    self.activeCollection = ko.observable(null);
    self.newCollectionVM = ko.observable(null);
    self.newSlideVM = ko.observable(null);
    self.slideDetailVM = ko.observable(null);
    self.calibrateVM = ko.observable(null);

    self.refreshCollectionList = function() {
        $.ajax({
            url: apiPrefix + "collection/list",
            cache: false,
            success: function(serverCols)
            {
                $("#collection-list").show();
                $("#collection-loading-spinner").hide();
                self.myCollections(serverCols);
            }
        });
    }

    self.switchView = function(view, data) {
        switch (view) {
            case CurrentView.MASTER:
                self.refreshCollectionList();
                self.activeCollection(null);
                self.currentView(view);
                break;
            case CurrentView.DETAIL:
                console.log(data);
                $.ajax({ url: apiPrefix + "collection?id=" + data.Id, type: "GET" })
                .done(function (col) {
                    self.activeCollection(col);
                    console.log(col);
                    self.currentView(view);
                })
                break;
            case CurrentView.ADD_COLLECTION:
                self.newCollectionVM(new AddCollectionViewModel());
                self.currentView(view);
                break;
            case CurrentView.ADD_SLIDE_RECORD:
                self.newSlideVM(new RecordSlideViewModel(self.activeCollection()));
                self.currentView(view);
                break;
            case CurrentView.SLIDE_DETAIL: 
                console.log(data);
                self.slideDetailVM(new SlideDetailViewModel(data));
                self.slideDetailVM().loadCalibrations();
                self.currentView(view);
                self.slideDetailVM().viewer = new CalibrationImage();
                self.slideDetailVM().viewer.init("static-image-previewer");
                break;
            case CurrentView.CALIBRATE: 
                self.calibrateVM(new CalibrateViewModel());
                self.calibrateVM().refreshMicroscopes();
                self.currentView(view);
                break;
        }
    }

    self.submitSlideRequest = function() {
        let request = self.newSlideVM().getRequest();
        $.ajax({
            url: "/api/v1/digitise/collection/slide/add",
            type: "POST",
            data: JSON.stringify(request),
            dataType: "json",
            contentType: "application/json"
        })
        .done(function (data) {
            self.switchView(CurrentView.DETAIL, self.activeCollection());
        })
    }

    self.setActiveCollectionTab = function(element) {
        $(element).parent().find("li").removeClass("active");
        $(element).addClass("active");
    }
}

////////////////////////
/// Create R. Collection
////////////////////////

function AddCollectionViewModel() {
    let self = this;
    self.name = ko.observable();
    self.description = ko.observable();

    self.submit = function(rootVM) {
        let req = {
            Name: self.name(),
            Description: self.description()
        };
        $.ajax({
            url: "/api/v1/digitise/collection/start",
            type: "POST",
            data: JSON.stringify(req),
            dataType: "json",
            contentType: "application/json",
            success: function() {
                rootVM.switchView(CurrentView.MASTER);
            },
            error: function(errors) {
                // Handle validation errors here
            }
        })
    }
}

////////////////////////
/// Record Slide Dialog
////////////////////////

function RecordSlideViewModel(currentCollection) {
    let self = this;
    self.collection = ko.observable(currentCollection);
    self.rank = ko.observable("");
    self.family = ko.observable("");
    self.genus = ko.observable("");
    self.species = ko.observable("");
    self.author = ko.observable("");
    self.newSlideTaxonStatus = ko.observable(null);
    self.currentTaxon = ko.observable();
    self.collectionMethod = ko.observable();
    self.existingId = ko.observable();
    self.yearCollected = ko.observable();
    self.nameOfCollector = ko.observable();
    self.locality = ko.observable();
    self.district = ko.observable();
    self.country = ko.observable();
    self.region = ko.observable();
    self.yearPrepared = ko.observable();
    self.preperationMethod = ko.observable();
    self.mountingMaterial = ko.observable();

    self.isValidTaxonSearch = ko.computed(function() {
        if (self.rank() == "Family" && self.family().length > 0) return true;
        if (self.rank() == "Genus" && self.family().length > 0 && self.genus().length > 0) return true;
        if (self.rank() == "Species" && self.genus().length > 0 && self.species().length > 0) return true;
        return false;
    }, self);

    self.isValidAddSlideRequest = ko.computed(function() {
        if (self.rank() == "") return false;
        if (self.currentTaxon() == "") return false;
        if (self.collectionMethod() == "") return false;
        return true;
    }, self)

    self.validateTaxon = function() {
        var query;
        if (self.rank() == "Family") {
            query = "rank=Family&family=" + self.family() + "&latinname=" + self.family();
        } else if (self.rank() == 'Genus') {
            query = "rank=Genus&family=" + self.family() + "&genus=" + self.genus() + "&latinname=" + self.genus();
        } else if (self.rank() == "Species") {
            query = "rank=Species&family=" + self.family() + "&genus=" + self.genus() + "&species=" + self.species() + "&latinname=" + self.genus() + " " + self.species() + "&authorship=" + self.author();
        }
        $.ajax({
            url: "/api/v1/backbone/trace?" + query,
            type: "GET"
        })
        .done(function(data) {
            if (data.length == 1 && data[0].TaxonomicStatus == "accepted") self.currentTaxon(data[0].Id);
            self.newSlideTaxonStatus(data);
        })
    }

    self.getRequest = function() {
        let request = {
            Collection: self.collection().Id,
            ExistingId: self.existingId(),
            OriginalFamily: self.family(),
            OriginalGenus: self.genus(),
            OriginalSpecies: self.species(),
            OriginalAuthor: self.author(),
            ValidatedTaxonId: self.currentTaxon(),
            SamplingMethod: self.collectionMethod(),
            YearCollected: parseInt(self.yearCollected()),
            YearSlideMade: parseInt(self.yearPrepared()),
            LocationRegion: self.region(),
            LocationCountry: self.country(),
            PreperationMethod: self.preperationMethod(),
            MountingMaterial: self.mountingMaterial()
        };
        return request;
    }
}

////////////////////////
/// Add Image View
////////////////////////

function SlideDetailViewModel(detail) {
    let self = this;
    self.slideDetail = ko.observable(detail);
    self.viewer = null;
    self.validationErrors = ko.observableArray([]);

    self.calibrations = ko.observableArray();

    // Add slide request
    self.isFocusImage = ko.observable(false);
    self.framesBase64 = ko.observableArray([]);
    self.floatingCal1x = ko.observable();
    self.floatingCal1y = ko.observable();
    self.floatingCal2x = ko.observable();
    self.floatingCal2y = ko.observable();
    self.measuredDistance = ko.observable();
    self.calibrationId = ko.observable();
    self.magnification = ko.observable();
    self.digitisedYear = ko.observable();

    self.imageSelected = function(inputElement, viewerId) {
        let imageUrls = [];
        Array.prototype.forEach.call(inputElement.files, function(f) { imageUrls.push(window.URL.createObjectURL(f)); });
        self.viewer = new Viewer("#" + viewerId, 500, 500, imageUrls);
        if (imageUrls.length > 1) { new FocusSlider(self.viewer) };
        let scaleBar = new ScaleBar(self.viewer, 0.5);
    }

    self.loadCalibrations = function() {
        $.ajax({ url: "/api/v1/digitise/calibration/list", type: "GET" })
        .done(function (cals) { self.calibrations(cals); })
    }

    self.submit = function(rootVM) {
        let request = {
            CollectionId: self.slideDetail().CollectionId,
            SlideId: self.slideDetail().CollectionSlideId,
            IsFocusImage: self.isFocusImage(),
            FramesBase64: [self.viewer.getBase64()], //TODO currently hardcoded static image
            FloatingCalPointOneX: self.viewer.getPointOneX(),
            FloatingCalPointOneY: self.viewer.getPointOneY(),
            FloatingCalPointTwoX: self.viewer.getPointTwoX(),
            FloatingCalPointTwoY: self.viewer.getPointTwoY(),
            MeasuredDistance: self.measuredDistance(),
            CalibrationId: self.calibrationId(),
            Magnification: self.magnification(),
            DigitisedYear: self.digitisedYear()
        }
        console.log(request);
        $.ajax({
            url: "/api/v1/digitise/collection/slide/addimage",
            type: "POST",
            data: JSON.stringify(request),
            dataType: "json",
            contentType: "application/json",
            success: function(data) {
                rootVM.switchView(CurrentView.DETAIL, self.slideDetail().CollectionId);
            },
            statusCode: {
                400: function(err) {
                    console.log(err);
                    self.validationErrors([]);
                    err.responseJSON.Errors.forEach(function(e) {
                        self.validationErrors().push(e.Errors[0]);
                        console.log(self.validationErrors());
                    })
                },
                500: function(data) {
                    self.validationErrors(['Internal error. Please try again later.']);
                }
            }
        })
    }
}

////////////////////////
/// Calibrate View
////////////////////////

var CalibrateView = {
  MASTER: 1,
  DETAIL: 2,
  ADD_MICROSCOPE: 3
};

function CalibrateViewModel() {
    let self = this;
    self.currentView = ko.observable(CalibrateView.MASTER);
    self.myMicroscopes = ko.observableArray([]);
    self.newMicroscope = ko.observable(null);
    self.microscopeDetail = ko.observable(null);

    self.refreshMicroscopes = function() {
        $.ajax({ url: "/api/v1/digitise/calibration/list", type: "GET" })
        .done(function (cals) {
            self.myMicroscopes(cals);
        })
    }

    self.changeView = function(view, data) {
        if (view == CalibrateView.MASTER) {
            self.refreshMicroscopes();
            self.microscopeDetail(null);
            self.newMicroscope(null);
            self.currentView(view);
        }
        else if (view == CalibrateView.DETAIL) {
            self.microscopeDetail(new ImageCalibrationViewModel(data));
            self.newMicroscope(null);
            self.currentView(view);

            self.microscopeDetail().calibrationImage = new CalibrationImage();
            self.microscopeDetail().calibrationImage.init("calibration-image-container");
        }
        else if (view == CalibrateView.ADD_MICROSCOPE) {
            self.newMicroscope(new MicroscopeViewModel());
            self.microscopeDetail(null);
            self.currentView(view);
        }
    }
}

function MicroscopeViewModel() {
    let self = this;
    self.friendlyName = ko.observable();
    self.microscopeType = ko.observable();
    self.ocular = ko.observable();
    self.microscopeModel = ko.observable("FAKE MICROSCOPE");
    self.magnifications = ko.observableArray([10,20,40,100]);

    self.addMag = function () {
        self.magnifications.push();
    }

    self.removeMag = function(mag) {
        self.magnifications.remove(mag);
    }

    self.submit = function(parentVM) {
        let request = {
            Name: self.friendlyName(),
            Type: self.microscopeType(),
            Model: self.microscopeModel(),
            Ocular: self.ocular(),
            Objectives: self.magnifications()
        };
        console.log(request);
        $.ajax({
            url: "/api/v1/digitise/calibration/use",
            type: "POST",
            data: JSON.stringify(request),
            dataType: "json",
            contentType: "application/json"
        })
        .done(function (data) {
            parentVM.changeView(CalibrateView.MASTER);
        })
    }
}

function ImageCalibrationViewModel(currentMicroscope) {
    let self = this;
    console.log(currentMicroscope);
    self.microscope = ko.observable(currentMicroscope);
    self.magnification = ko.observable(40);
    self.startPoint = ko.observable([2,5]);
    self.endPoint = ko.observable([88,21]);
    self.measuredLength = ko.observable(10);
    self.calibrationImage = null;

    self.submitCalibration = function(parent) {
        let request = {
            CalibrationId: self.currentMicroscope().Id,
            Magnification: self.magnification(),
            X1: self.startPoint()[0],
            X2: self.endPoint()[0],
            Y1: self.startPoint()[1],
            Y2: self.endPoint()[1],
            MeasuredLength: self.measuredLength(),
            ImageBase64: self.calibrationImage.getBase64()
        }
        console.log(request);
        $.ajax({
            url: "/api/v1/digitise/calibration/use/mag",
            type: "POST",
            data: JSON.stringify(request),
            dataType: "json",
            contentType: "application/json"
        })
        .done(function (data) {
            parent.currentView(CalibrateView.MASTER);
        })
    }
}

// Helpers - Dropdown autocomplete
var typingTimer;
var doneTypingInterval = 100;

function suggest(entryBox, rank) {
    clearTimeout(typingTimer);
    if (entryBox.value) {
        typingTimer = setTimeout(function () {
            updateList(entryBox, rank);
        }, doneTypingInterval);
    }
};

//Update suggestion list when timeout complete
function updateList(entryBox, rank) {
    var query = '';
    if (rank == 'Species') {
        //Combine genus and species for canonical name
        var genus = document.getElementById('original-Genus').value;
        query += genus + " ";
    }
    query += entryBox.value;

    if (entryBox.value == "") {

    } else {
        var request = "/api/v1/backbone/search?rank=" + rank + "&latinName=" + query;
        $.ajax({
            url: request,
            type: "GET"
        }).done(function (data) {
            var list = document.getElementById(rank + 'List');
            $('#' + rank + 'List').css('display', 'block');
            list.innerHTML = "";
            for (var i = 0; i < data.length; i++) {
                if (i > 10) continue;
                var option = document.createElement('li');
                var link = document.createElement('a');
                option.appendChild(link);
                link.innerHTML = data[i];

                var matchCount = 0;
                for (var j = 0; j < data.length; j++) {
                    if (data[j].latinName == data[i]) {
                        matchCount++;
                    }
                };
                link.addEventListener('click', function (e) {
                    var name = this.innerHTML;
                    if (rank == 'Species') {
                        $('#original-Species').val(name.split(' ')[1]).change();
                        $('#original-Genus').val(name.split(' ')[0]).change();
                    } else if (rank == 'Genus') {
                        $('#original-Genus').val(name).change();
                    } else if (rank == 'Family') {
                        $('#original-Family').val(name).change();
                    }
                    $('#' + rank + 'List').fadeOut();
                });
                list.appendChild(option);
            }
        });
    }
}


function disable(rank) {
    var element;
    if (rank == 'Family') element = 'FamilyList';
    if (rank == 'Genus') element = 'GenusList';
    if (rank == 'Species') element = 'SpeciesList';

    setTimeout(func, 100);
    function func() {
        $('#' + element).fadeOut();
    }
}


function CalibrationImage() {
    let self = this;
    self.canvas = null;
    self.svg = null;
    self.line = null;

    self.height = 300;
    self.width = 400;

    self.base64 = null;

    self.init = function(containerId) {
        d3.select("#" + containerId)
        .style('position', 'relative');

        self.canvas = d3.select("#" + containerId)
            .append("canvas") 
            .attr("width", self.width)
            .attr("height", self.height)
            .node().getContext('2d');

        self.svg = d3.select("#" + containerId)
            .append("svg") 
            .attr("width", self.width)
            .attr("height", self.height)
            .style('position', 'absolute')
            .style('top', 0)
            .style('left', 0)
            .on("mousedown", self.mousedown)
            .on("mouseup", self.mouseup);
        self.line = self.svg.append("line");
    }

    self.changeImage = function(image) {
        let file = image.files[0];
        let reader = new FileReader();

        reader.onloadend = function (onloadend_e) 
        {
            self.base64 = reader.result;
            let img = new Image();
            img.onload = function() {
                self.canvas.drawImage(img, 0, 0, img.width, img.height,
                                           0, 0, self.width, self.height);
            };
            img.src = self.base64;
        };

        if(file)
        {
            reader.readAsDataURL(file);
        }
    }

    self.getPointOneX = function() { return self.line.attr('x1') }
    self.getPointOneY = function() { return self.line.attr('y1') }
    self.getPointTwoX = function() { return self.line.attr('x2') }
    self.getPointTwoY = function() { return self.line.attr('y2') }

    self.getBase64 = function() {
        return self.base64;
    }

    self.mousedown = function() {
        var m = d3.mouse(this);
        self.line
            .attr("x1", m[0])
            .attr("y1", m[1])
            .attr("x2", m[0])
            .attr("y2", m[1])
            .attr('stroke', 'red');
        
        self.svg.on("mousemove", self.mousemove);
    }

    self.mousemove = function() {
        var m = d3.mouse(this);
        self.line.attr("x2", m[0])
            .attr("y2", m[1]);
    }

    self.mouseup = function() {
        self.svg.on("mousemove", null);
        $('#startPoint').val(self.line);
    }
}

//Base Functions
function convertToDataURLviaCanvas(url, callback) {
    var img = new Image();
    img.crossOrigin = 'Anonymous';
    img.onload = function () {
        var canvas = document.createElement('CANVAS');
        var ctx = canvas.getContext('2d');
        var dataURL;
        canvas.height = this.height;
        canvas.width = this.width;
        ctx.drawImage(this, 0, 0);
        dataURL = canvas.toDataURL("image/png");
        callback(dataURL);
        canvas = null;
    };
    img.src = url;
}