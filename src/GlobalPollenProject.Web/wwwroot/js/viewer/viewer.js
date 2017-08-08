/**
 * Creates a new slide image canvas - allows panning and zooming, and ensures the slide is always visible
 * @param {String} containerId  the parent object's id
 * @param {Int} width           the desired width of the canvas
 * @param {Int} height          the desired height of the canvas
 * @param {[String]} imagePaths an array of image paths
 * @return {Viewer}             the relevant viewer object
 */
function Viewer(containerId, width, height, imagePaths) {
    var self = this;

    self.id = "#viewer-canvas";

    // holds the function parameters
    self.containerId = containerId;
    self.width = width;
    self.height = height;
    self.imagePaths = imagePaths

    // stores the loaded image objects
    self.images = [];
    self.loadedCounter = 0;

    self.base = null; // the parent container of the whole viewer
    self.canvas = null; // the canvas element - displays the image of the slide
    self.transform = null; // zoom/pan transform
    self.context = null; // the canvas 2d context - for drawing on the canvas
    self.imgWidth = null; // stores the width of the slide image
    self.imgHeight = null; // stores the height of the slide image
    
    self.focusLevel = 0; // holds the current focus level (image to draw)

    /**
     * Populates the "images" array with image objects loaded using image paths
     * Will throw an error if the images are not equal sized
     */
    self.loadImages = function (callback) {
        // loop through all image paths (focus levels), and load the images
        for (var i = 0; i < self.imagePaths.length; i++) {
            var img = new Image();
            img.onload = function () {
                // ensure all focus level images have the same dimensions
                if (self.imgWidth != undefined && self.imgHeight != undefined) {
                    if (this.width != self.imgWidth || this.height != self.imgHeight) {
                        console.error("Focus images are not of equal size! Size of image #" + i + ": " +
                            this.width + "x" + this.height + " - expected size: " + 
                            self.imgWidth + "x" + self.imgHeight);
                    }
                } else {
                    self.imgWidth = this.width;
                    self.imgHeight = this.height;
                }

                self.loadedCounter++;
                if (self.loadedCounter == self.imagePaths.length) {
                    // proceed if all images have been loaded - trigger a jQuery function too
                    callback();
                    $(self).trigger(Viewer.EVENT_LOADED_IMAGES);
                }
            }
            img.src = self.imagePaths[i];
            self.images.push(img);
        }
    }

    /**
     * Creates the <canvas> element, and initialises panning + zooming through d3
     */
    self.createCanvas = function (callback) {

        // sub-function - zoom callback - gets called when canvas is zoomed
        function zoomed() {
            if (self.transform.k > d3.event.transform.k) {
                $(self.id).css("cursor", "zoom-out");
            } else if (self.transform.k < d3.event.transform.k) {
                $(self.id).css("cursor", "zoom-in");
            } else {
                $(self.id).css("cursor", "move");
            }

            if (d3.event.transform.x > self.width / 2) 
                d3.event.transform.x = self.width / 2;
            if (d3.event.transform.y > self.height / 2) 
                d3.event.transform.y = self.height / 2;

            if (d3.event.transform.x < self.width / 2 - self.imgWidth * d3.event.transform.k) 
                d3.event.transform.x = self.width / 2 - self.imgWidth * d3.event.transform.k;
            if (d3.event.transform.y < self.height / 2 - self.imgHeight * d3.event.transform.k) 
                d3.event.transform.y = self.height / 2 - self.imgHeight * d3.event.transform.k;

            self.transform = d3.event.transform;

            // trigger a zoom event
            $(self).trigger(Viewer.EVENT_ZOOMED);
        }

        // create the canvas element
        self.base = d3.select(self.containerId);
        self.canvas = self.base.append("canvas")
            .attr("id", self.id.substr(1))
            .attr("width", self.width)
            .attr("height", self.height)
            .call(d3.zoom()
                .extent([
                    [-self.imgWidth / 2, -self.imgHeight / 2],
                    [self.imgWidth + self.imgWidth / 2, self.imgHeight + self.imgHeight / 2]
                ])
                .scaleExtent([0.5, 4])
                .on("zoom", function() {
                    zoomed();
                    self.render();
                })
                .on("end", function() {
                    $(self.id).css("cursor", "grab");
                }));
        self.transform = d3.zoomIdentity;
        self.context = self.canvas.node().getContext("2d");

        $(self.id).css("cursor", "grab"); // set the cursor to "grab" initially

        // stop the page from scrolling when zooming with the mouse wheel
        $(self.id).bind("wheel mousewheel", function (e) {
            e.preventDefault()
        });

        // initialisation is complete - proceed through callback
        callback();
    }

    /**
     * To be called when the canvas needs to be redrawn
     */
    self.render = function() {
        self.context.save();
        
        // clear the screen
        self.context.clearRect(0, 0, self.width, self.height);
        self.context.fillStyle = "#777777";
        self.context.fillRect(0, 0, self.width, self.height);

        // draw the correct slide image in the correct location
        self.context.translate(self.transform.x, self.transform.y);
        self.context.scale(self.transform.k, self.transform.k);
        self.context.shadowColor = '#555555';
        self.context.shadowBlur = 20;
        self.context.shadowOffsetX = 15;
        self.context.shadowOffsetY = 15;
        self.context.drawImage(self.images[self.focusLevel], 0, 0);

        self.context.restore();
    }

    /**
     * Switches the displayed image to the intended focus level
     */
    self.setFocusLevel = function(level) {
        if(level < 0) level = 0;
        if(level > self.getMaxFocusLevel()) level = self.getMaxFocusLevel();
        self.focusLevel = level;
        self.render();
    }

    /**
     * Returns the maximum possible focus level (minimum is always 0)
     */
    self.getMaxFocusLevel = function() {
        return self.images.length - 1;
    }

    /**
     * Returns the current zoom level
     */
    self.getZoom = function() {
        return self.transform.k;
    }

    // ENTRY POINT - functions have all been defined
    self.loadImages(function() {
        self.createCanvas(function() {
            self.render();
        });
    });
}

Viewer.EVENT_LOADED_IMAGES = "loadedImages";
Viewer.EVENT_ZOOMED = "zoomed";