"""Downloads part of a public speech test set that comes with reference transcripts.

Usage:
  python fetch_testsets.py fleurs LANGUAGE COUNT OUT
  python fetch_testsets.py argentinian COUNT OUT [PATTERN]

`fleurs` takes the first COUNT recordings of the test split of FLEURS (google/fleurs on Hugging
Face, CC BY 4.0) for a language such as es_419 or en_us. FLEURS is the test set that the Gemma 4
model card reports its speech recognition results on. The audio is one archive per split, so the
script reads the archive as a stream and stops after COUNT files.

`argentinian` takes COUNT recordings, half from women and half from men, of Google's crowdsourced
Argentinian Spanish corpus (OpenSLR 61, ylacombe/google-argentinian-spanish on Hugging Face,
CC BY-SA 4.0), whose sentences include voseo. With PATTERN, a regular expression, it takes only
recordings whose sentence matches, such as the ones with a voseo verb.

Both write OUT/audio/*.wav and OUT/refs.tsv, with one "file name, tab, reference text" line per
recording. OUT must be a folder that git ignores.
"""
import json
import re
import sys
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

HUB = "https://huggingface.co/datasets"
ROWS = "https://datasets-server.huggingface.co/rows"


def open_url(url):
    # The dataset server answers 429 or 5xx now and then; a later attempt gets through.
    for attempt in range(6):
        try:
            return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "local-whisper-mcp-poc"}), timeout=120)
        except urllib.error.HTTPError as error:
            if attempt == 5 or (error.code != 429 and error.code < 500):
                raise
            time.sleep(5 * 2 ** attempt)


def fetch_fleurs(language, count, out):
    with open_url(f"{HUB}/google/fleurs/resolve/main/data/{language}/test.tsv") as response:
        rows = [line.split("\t") for line in response.read().decode("utf-8").splitlines()]

    # Columns: sentence id, file name, raw transcription, normalized transcription, characters, samples, gender.
    texts = {row[1]: row[2] for row in rows if len(row) >= 3}
    refs = []
    received = 0
    with open_url(f"{HUB}/google/fleurs/resolve/main/data/{language}/audio/test.tar.gz") as response:
        with tarfile.open(fileobj=response, mode="r|gz") as archive:
            for member in archive:
                name = Path(member.name).name
                if not member.isfile() or name not in texts:
                    continue
                data = archive.extractfile(member).read()
                (out / "audio" / name).write_bytes(data)
                refs.append((name, texts[name]))
                received += len(data)
                if len(refs) >= count:
                    break
    return refs, received


def argentinian_rows(config, offset, length):
    query = urllib.parse.urlencode({"dataset": "ylacombe/google-argentinian-spanish", "config": config, "split": "train", "offset": offset, "length": length})

    # The dataset server limits the request rate of anonymous clients.
    time.sleep(0.5)
    with open_url(f"{ROWS}?{query}") as response:
        return json.load(response)["rows"]


def fetch_argentinian(count, out, pattern):
    corpus = (("female", 3921), ("male", 1818))
    if pattern is None:
        # Blocks spread over the corpus reach more speakers than its first rows do.
        wanted = []
        for config, total in corpus:
            blocks = max(1, -(-(count // 2) // 50))
            wanted += [(config, block * (total // blocks) + index) for block in range(blocks) for index in range(min(50, count // 2 - block * 50))]
    else:
        # The sentences that match are few, so the script reads every sentence first and then
        # takes matches spread over the corpus.
        matches = [
            (config, row["row_idx"])
            for config, total in corpus
            for offset in range(0, total, 100)
            for row in argentinian_rows(config, offset, 100)
            if re.search(pattern, row["row"]["text"], flags=re.IGNORECASE)
        ]
        step = max(1, len(matches) // count)
        wanted = matches[::step][:count]
        print(f"{len(matches)} sentences match", flush=True)

    refs = []
    received = 0
    for config, index in wanted:
        # The audio links expire, so each recording gets a fresh one.
        row = argentinian_rows(config, index, 1)[0]
        name = f"{config}-{index:04d}.wav"
        with open_url(row["row"]["audio"][0]["src"]) as response:
            data = response.read()
        (out / "audio" / name).write_bytes(data)
        refs.append((name, row["row"]["text"]))
        received += len(data)
    return refs, received


arguments = sys.argv[1:]
if len(arguments) == 4 and arguments[0] == "fleurs":
    out = Path(arguments[3])
    (out / "audio").mkdir(parents=True, exist_ok=True)
    refs, received = fetch_fleurs(arguments[1], int(arguments[2]), out)
elif len(arguments) in (3, 4) and arguments[0] == "argentinian":
    out = Path(arguments[2])
    (out / "audio").mkdir(parents=True, exist_ok=True)
    refs, received = fetch_argentinian(int(arguments[1]), out, arguments[3] if len(arguments) == 4 else None)
else:
    sys.exit(__doc__)

with open(out / "refs.tsv", "w", encoding="utf-8", newline="\n") as file:
    for name, text in sorted(refs):
        file.write(f"{name}\t{' '.join(text.split())}\n")
print(f"{len(refs)} recordings, {received / 1e6:.0f} MB of audio, in {out}")
