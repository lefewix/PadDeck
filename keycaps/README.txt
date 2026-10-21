PadDeck keycaps
===============

Files are in the "out" folder. All of them are already lying the right way for printing
(top of the cap flat on the bed). No supports needed.

1. Print stem-fit-test.stl first (about 5 minutes).
   - It has 5 little stems. 1 dot = tightest, 5 dots = loosest.
   - Push each one onto a switch. Pick the one that holds firmly but still comes off.
   - The caps use number 3 (1.30 mm). If you need a different one, change CROSS_W in
     make_keycaps.py (1.20 / 1.25 / 1.30 / 1.35 / 1.40) and run it again.

2. Print the caps.
   - One colour printer: out\single-color\*.stl. The icon is cut 0.6 mm into the top.
     You can fill it with paint or a paint pen afterwards.
   - AMS / multi-colour printer: out\two-color\*.3mf. Each file has two parts: the cap and
     the icon. If the slicer asks, load it as ONE object with multiple parts, then set the
     icon part to your teal filament.

3. Knob (optional). Pull the old knob off and look at the metal shaft:
   - Has a flat side -> knob-d-shaft.stl
   - Round with fine ridges -> knob-round-shaft.stl

Print settings: 0.2 mm layers, 15-20% infill, 3 walls. PLA or PETG both work.

To regenerate: python make_keycaps.py --preview   (needs manifold3d, shapely, trimesh, numpy,
matplotlib for the preview)
