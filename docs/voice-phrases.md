# Named voice phrases

Version `1.0.1-rx.5` adds separate lists for saving clips and taking screenshots. Each action can hold five phrases, including disabled ones. Names are labels, not automatic speech transcription. Recognition remains local with the existing models and sensitivity settings.

## Use

Open **Voice saving**, enter a name under the desired action, and choose **Add and record**. Say the same short, distinct phrase three times, leaving a pause between attempts. A phrase becomes part of the list only after training completes. Canceling a new recording adds nothing; canceling a replacement keeps its previous samples.

Each row can be renamed, enabled or disabled, tested, recorded again, or deleted. List edits are saved immediately. The global voice switch and sensitivity still use the settings window's Apply/Save flow. Testing reports the recognized name and does not save a clip or screenshot. Disabled phrases can be tested individually without enabling normal listening.

The existing clip and screenshot phrases are imported once, preserving their original files. The list is stored separately from capture settings. Migration, renaming and activation do not change video compression, replay duration, hotkeys, or microphone levels.

## Recognition and resource use

The listener prepares one speech embedding per utterance and compares it against cached phrase templates. Normalized templates are reused, and dynamic time warping needs three rolling rows instead of a full matrix per template. The ten-phrase limit bounds comparison work; disabled entries do not participate in normal recognition.

Enrollment checks for confusion with phrases assigned to the other action. At runtime, ambiguous cross-action matches are rejected rather than selecting an action arbitrarily. Each action retains its own ten-second cooldown, so two clip phrases do not bypass the clip cooldown.

Three samples and rejection checks help distinguish commands; they do not guarantee perfect recognition in live conversation or game audio. Use distinct phrases, try normal conversation in test mode, and keep only the alternatives you need.

## Matcher measurement

A synthetic benchmark compared the original matcher with the prepared implementation on the same ten phrases, nine templates each, 96 embedding dimensions and 38 query frames. Three alternating rounds of 100 utterances used the same inputs with tiered JIT disabled.

| Component measurement | Original | Prepared |
| --- | --- | --- |
| Allocated bytes per utterance, including preparation | 3,436,880 | 16,864 |
| Median comparison time per utterance | 5.55 ms | 3.58 ms |
| Allocations while comparing an already prepared utterance | — | 0 B |

These figures exclude speech-model inference, enrollment and the rest of the application. They do not establish game FPS or total application memory savings. Numerical comparisons against the original full-matrix algorithm preserve its matching scores.

## Validation

- Release x64 compilation: zero warnings and errors.
- All 34 new regression checks pass: prepared matcher (6), WAV bounds (3), phrase bank (8), real-model listener integration (5), and list UI/lifetime (12).
- The complete final suite passes 640 of 641 checks. The sole failure is `ShareTranscodeTests.An_encoder_overshoot_is_retried_until_the_file_fits`, the same 0.4 MB export-size failure previously reproduced on pristine upstream `04fd8bf` on this PC.
- Existing positive, near-phrase and conversation fixtures continue to pass. Rolling-row alignment matches the original algorithm exactly on 350 varied comparisons. All 19 voice WAV fixtures decode to the same 717,174 samples as before.
- Migration on an isolated copy of this PC's existing data retained both phrases and all six WAVs byte for byte. The live data is backed up before the application update.
- Synthetic list, training, testing and empty states were visually checked at normal or minimum window dimensions in Spanish, Arabic and German. All seven languages pass completeness, placeholder and short-note checks.
- Pending enrollment, cancellation after a transport reply, repeated Cancel clicks, window closure and restart have explicit regression coverage. The settings process waits for bounded voice cleanup after its window closes.

The ambiguity guard requires a normalized cross-action score difference of at least 0.10 when both actions pass their thresholds. This is a conservative routing rule, not a probability or a measured human recognition improvement. Live microphone precision and game FPS still need the user's normal-use comparison.
