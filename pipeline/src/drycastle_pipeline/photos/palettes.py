"""The named colours a building can be given (the pipeline's lists, and the viewer's colour pickers).

Kept apart from ``facade_pipeline`` (numpy, camera maths) so the building worker can offer them without loading any of that;
``facade_pipeline`` re-exports all four.
"""

# what the walls are painted or built with, as it looks in plain daylight. A wall in shade photographs grey whatever
# its paint, so the colour is named, not sampled.
# (the village's palette: every named colour is one chosen paint, warm and a little richer than a photo shows it)
WALL_COLOURS = {"white": "#f1ecdf", "cream": "#ecdcb2", "yellow": "#e9ca76", "peach": "#e8b88a", "orange": "#dc9654", "pink": "#e2ab9d",
                "green": "#b3c896", "blue": "#a4bbd3", "grey": "#b4b1a8", "beige": "#d3ba92", "brown": "#906746", "brick": "#a6543a", "timber": "#73502e",
                "stone": "#a49c8a"}  # fmt: skip
SURROUND_COLOURS = {"white": "#f2f0ea", "cream": "#e9dcc0", "grey": "#a9a8a4", "green": "#5e9a5a", "brown": "#6b4a3a", "dark": "#3a3a3c"}  # the band round a window
ROOF_COLOURS = {"anthracite": "#41424a", "grey": "#7e8088", "brown": "#704533", "red": "#a5442e", "orange": "#bf6a38", "green": "#4c6c50"}
FRAME_COLOURS = {"white": "#f2f0ea", "brown": "#5a4030", "dark": "#3a3a3c", "wood": "#a87f55", "grey": "#9a9a9a"}
