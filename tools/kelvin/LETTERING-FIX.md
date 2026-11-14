# Kelvin hull lettering: required DXT1 input

In release 0.7.41 the installed game's log reports all three lettering textures as
`compressed to DXT5, not DXT1; its lettering stays as it is`, followed by `has no blocks kept`.
This affects the saucer and both nacelles. The crew name is correctly configured and
Michroma is present; neither the font nor the name setting causes this failure.

The optimized lettering painter explicitly consumes and writes DXT1 blocks. Keeping
that worker-thread path is desirable: it avoids rebuilding/compressing the entire
texture whenever the crew name changes. But that makes DXT1 an input contract, not
an optional optimization. DXT5 cannot be passed to that painter: its block layout and
size differ, so removing the format check or treating its bytes as DXT1 would corrupt
textures. A DXT5 painter would be a separate implementation.

Constructing a texture as RGB24 before LoadImage does not enforce its final format:
LoadImage can replace the format with an alpha-bearing one. Compress then yields DXT5,
as demonstrated by the release log. Even wholly opaque PNG content can take this path.

Fix: only for explicitly configured, non-linear lettering textures, inspect the decoded
alpha. If every pixel is opaque, copy once into an explicit RGB24 texture, regenerate its
mips, then use the existing compression and DXT1 block retention. Free the temporary
original. Do not reconvert for each name update. Preserve genuine transparency and the
existing unsupported-format warning. Other hull textures, normal/gloss maps and ships
without configured lettering textures keep their existing loading path.

Validation: every pixel in the three installed affected textures has alpha 255:
2048x2048 saucer, 2000x284 starboard and port nacelles. The main plugin builds against
the installed game/Unity/BepInEx assemblies (four existing warnings); diff whitespace
checks pass. Actual Unity compression and successful in-game painting still need a
runtime check. Expected: all three DXT5/no-block warnings disappear, then the custom
crew name and registry appear on the saucer and both nacelles. Check a changed name
and a second mission as well. This patch has not been installed over the user's release.
