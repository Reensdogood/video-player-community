import sys
import whisper
import torch
from whisper.utils import get_writer

source, model_path, output_dir = sys.argv[1:]
device = "cuda" if torch.cuda.is_available() else "cpu"
model = whisper.load_model(model_path, device=device)
result = model.transcribe(source, task="transcribe", fp16=device == "cuda", verbose=False)
get_writer("srt", output_dir)(result, "caption.wav")
