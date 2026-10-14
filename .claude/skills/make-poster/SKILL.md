---
name: make-poster
description: Make Club Elementum staff/role posters (1440x3440 PNG in Posters/AI) from a Name / Role / Element block plus an avatar image, with element-themed name lettering and hand-picked framing. Use this whenever the user pastes a "Name: … Role: … Element: …" block, attaches an avatar render and asks for a poster, says "make a poster for <person>", or asks to reframe, resize, move or redo a poster in Posters/AI, even if they don't say "skill" or "poster template".
---

# Make a Club Elementum poster

A poster is a 1440x3440 PNG built from the layered templates in `Posters/templates`. The layers, from back to front, are:

1. the element background;
2. the person's avatar;
3. the element foreground, a frame of leaves, flames and so on;
4. the person's name, in gold lettering filled with the element's own artwork;
5. the role title;
6. the club logo.

`Tools/make_poster.py` does all the compositing. Your job is the part a designer would do by hand: study the avatar, decide how to frame it, and check the result. The user cares about this. They want each poster to look composed for that person, not mechanically centred.

## Input

The user sends a block like this, with an image:

```
Name: Rosebud
Role: Dance Captain
Element: Water
```

- **The image:** in Claude Code, an attached image appears as `[Image: source: <path>]`; use that path. If there's no file path, ask the user to save the image and give you the path.
- **Several blocks:** make one poster per block.
- **Elements:** water, fire, earth, wind ("air" means wind).
- **Roles:** Host, Lead Host, DJ, Dancer, Dance Captain, Photographer, Security, Lead Security (fire only, for now). Spelling and spacing don't matter ("Dancecaptain" works).
- **Custom titles:** owners, co-owners and "head of …" titles are one-off posters the user makes by hand. If asked for one, say the skill only covers the standard role titles.

## Workflow

Run Python as `py -3`. On this Windows machine, `python` is the Microsoft Store stub. The script needs Pillow, which is installed in the user's site-packages, so don't run with `-I`.

Always use the script's command line. Don't import `Tools/make_poster.py` from your own Python code; that leaves a `__pycache__` folder in the repo. Put scratch files (grids, previews, crops) in your scratchpad or temp folder, not in the repo.

### 1. Inspect the avatar

```
py -3 Tools/make_poster.py --avatar <image> --inspect <scratch>/grid.png
```

It prints:
- the size and how much of the image is transparent;
- the subject's bounding box;
- any edge where the render is cut off;
- how dark the render is.

It also writes a copy of the image on grey, with a labelled grid. View the grid image and read avatar coordinates from its labels, such as where the faces are and where the visible top of the head is. The labels are always avatar pixels, even when the viewer shows the image shrunk.

The printed subject box can include faint strands above the hair, so take the visible top from the grid.

If the image has little or no transparency, stop and ask for a cut-out PNG. Don't try to remove a background yourself.

### 2. Decide the framing

Look at the picture as a designer would:
- How many characters are there?
- Where are the faces?
- Which way does each character look or gesture?
- Is there a hero element, like a chain arc, halo, wings or prop?
- Is the render a full figure or a bust?
- Is anything cut off?

Then choose a scale and a placement using the guidance under "Framing". For each, write down one sentence on why.

### 3. Render with a preview

```
py -3 Tools/make_poster.py --avatar <image> --name "<Name>" --role "<Role>" --element <element> \
    --scale S --focus FX,FY --at PX,PY --preview <scratch>/preview.png
```

- `--focus` is a point on the avatar, in avatar pixels read from the grid. Use the main face, or the midpoint between faces for a group.
- `--at` is where that point lands on the poster.
- To put the visible top of the head at poster height T: `PY = T + (FY - head_top) * S`.

The output goes to `Posters/AI/<role>-<name>.png`. That name is plain ASCII, lowercase, with hyphens. The user's posters are fetched over HTTP, so let the script choose the name and don't pass `--out` unless asked.

### 4. Check, then fix

**Read the script's output.** Fix any `warning: … cut-off … edge shows` before showing the poster; see "Cut-off edges". The one accepted exception is a flat top edge through a cropped head. The "enlarged … may look soft" warning just goes in your report.

**View the preview.** It is half size, so poster coordinates are twice what you see. It shows:
- the title band in magenta;
- the name zone in yellow;
- the logo in cyan;
- the edge of the foreground's open window in magenta;
- your `--at` point as a red cross.

Check that:
- every face is clear of the title, the name, the logo and the foreground;
- there is background showing between the title and the head.

**Zoom in** at full size on faces near the frame, and anywhere the foreground has see-through parts:

```
py -3 -c "from PIL import Image; Image.open(r'<poster>.png').crop((x0,y0,x1,y1)).save(r'<scratch>/zoom.png')"
```

Re-render with adjusted numbers until it looks right. Rendering is quick, a few seconds. Two or three attempts is normal.

### 5. Report

Keep the report short:
- a link to the poster;
- a sentence or two on the framing choices;
- anything the user should know: a softness warning, a name drawn with substitute letters, or a cut edge you couldn't hide.

Then invite tweaks. When the user says something like "a bit bigger and higher", re-render the same poster with adjusted `--scale` and `--at`. It overwrites the same file.

## Framing

These come from posters the user approved or corrected. When they pull against each other, apply them in this order:

1. faces clear;
2. room under the title;
3. cut edges hidden;
4. hero element whole;
5. size.

- **Keep faces clear.** No face under the title, name, logo or foreground. A partly covered face is the change the user asks for most often. Limbs, tails, capes, props and furniture can go behind the foliage.
- **Leave breathing room under the title.** Background should show between the bottom of the title and the top of the head: roughly 50 to 250px of it.
  - Heads usually start around y 770–950; the title occupies y 0–720, and Dance Captain titles end around y 690.
  - Don't push a head up into the title. The user turned down a close-up where a cropped head was tucked under the title to hide its flat edge, calling it too large.
  - The opposite fails too: a group sitting low under a big empty sky was moved up at the user's request.
  - A hero prop or effect, like a chain arc or halo, may reach up to or slightly under the title.
- **Go big, within those limits.** A standing full-body render reads best from head to roughly the knees: scale it so the whole figure would be about 3000px tall, and let the foreground and name hide the legs. Feet running off the bottom of the poster is fine. A bust or half-body render should be about 1600px wide, with its sides tucked behind the foliage and its head well below the title. It shouldn't become a giant close-up. The automatic framing in `--inspect` is a reasonable starting point for a single, front-facing figure.
- **Leave room for gaze and gesture.** If a character looks, turns or gestures to one side, shift them the other way so there's space in that direction. Examples: Skully's arms point right, so he sits left of centre. ShinedLight looks back over his shoulder to the left, so he sits right of centre.
- **Groups:** keep everyone together and fit every face inside the open window. Put the focus on the midpoint between faces. Diagonals, like one standing figure and one leaning figure, look good.
- **Keep the hero element whole** if it makes the pose, such as a chain swung overhead or a halo.
- **Sitting or wide poses:** put the face in the upper part of the window. Let the legs run off behind the foreground, and let the name sit under them like a pedestal.
- **Don't alter the avatar.** No opacity, colour, mirroring or erasing: only scale and position. `--backlight` adds a soft element-coloured glow behind the character without touching the image. Use it only when a dark render disappears into a dark background (often fire or earth), and check that it actually helps.
- **Expect softness above about 3x enlargement.** Mention it, and suggest a higher-resolution render.

## Poster layout (poster pixels)

| Area | Where |
|---|---|
| Title | y 0–720 (Dance Captain titles about 70–690). The bottom ornament dips lower in the middle than at the sides, and the art has gaps: some titles only fully cover down to about y 550. Measure "room under the title" from the lowest point of the ornament above the head. |
| Open window in the foreground | roughly x 200–1250 from y 800 to 2400; narrows to about x 500–1050 near y 2800; closes completely around y 3100–3130. The preview outlines it exactly. |
| Name | centred, capitals about y 2490–2710; long names shrink or wrap onto two lines |
| Gold rule (divider) | under the name: about y 2770–2810 for one line and about y 2840 for two; the script prints the exact y |
| Logo | bottom left, x 35–603, y 2894–3419 |

Each element's foreground has its own quirks to check against faces:

- **Earth:** a leaf sprig reaches in from the right at face height, around x 950–1130 and y 1110–1430. The usable window there is narrower than the table suggests.
- **Wind:** semi-transparent white ribbons cross the window, one around y 1490–1770. They read as wind wrapping the character and are fine over bodies, but keep them off faces. They're faint in the preview, so zoom in.
- **Water and fire:** fairly open windows. Check the leaves and flames along the sides.

## Cut-off edges

Many renders are cut off flat at an image edge, usually the bottom. `--inspect` lists these edges. A hard straight line showing on the poster looks broken, so hide it where you can:

1. **Bottom edge on the gold rule:** add `--bottom-on-rule`, with `--at` given as just the x (for example `--at 720`). The script moves the avatar vertically so its last row sits exactly on the gold rule under the name, and the character appears to stand on the nameplate. This is usually the best fix for busts and big groups; it was used for the trio and Rosebud posters.
2. **Bottom edge below the foreground:** the foreground fully closes around y 3130. This usually means a bigger scale.
3. **Side edges:** push them off the poster or behind the side foliage.
4. **Top edges of props or effects** (halo spikes, a chain): these may tuck under the title art. Check the warning, because the title has gaps.

A flat top edge through a cropped head is the exception. Don't hide it by pushing the head into the title; keep the room under the title and accept the edge. The script will still warn about it; that's expected. The user's own Twig poster (`Posters/originals/ho-twigbirchwood.png`) does exactly this. Mention it in your report.

## Names

- **Lettering:** the default "element" style (Cinzel Decorative). Capitals become decorative swash letters, so pass the name with the user's own casing. `--name-style classic` gives the older flat Vast Shadow look, but only use it if asked.
- **Numbers are intentional.** "Skul1y" stays "Skul1y". Never swap a digit for a letter or a letter for a digit. The script draws digits from a companion font (Playfair Display) so a 1 reads as a 1, not an I.
- **Look-alike letters:** display names often use Cyrillic or Greek letters that imitate Latin ones. The script draws and files these as Latin: "Jаcїпта" is drawn JACÏNTA and saved as `dancer-jacinta.png`. Tell the user when that happens.
- **Undrawable characters:** if the script fails on a character, such as an emoji, ask the user how they want it written, or pass `--display-name` with a version the font can draw.

## Script options

| Option | Use |
|---|---|
| `--inspect [GRID.png]` | describe the avatar and write the coordinate grid (no poster) |
| `--scale S` | avatar scale (1 = original pixels) |
| `--focus X,Y` / `--at X,Y` | place avatar point (X,Y) at poster point (X,Y); always used together (`--at X` with `--bottom-on-rule`) |
| `--bottom-on-rule` | put a cut-off bottom edge on the gold rule (element style only) |
| `--preview P.png` | half-size copy with layout guides |
| `--backlight` | soft element-coloured light behind a dark render |
| `--display-name` | draw a different spelling of the name |
| `--name-style classic` | old flat lettering (not the default) |

## Leave alone

Don't move posters into `Posters/originals/`, commit or push unless the user asks. Anything in `originals` gets published into the live VRChat world by a GitHub Action.
