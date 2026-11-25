"""The two local embedding models, loaded lazily once per process.

Text: any sentence-transformers model (Qwen3-Embedding, EmbeddingGemma, ...), prompts from config.
Image: SigLIP-family model through transformers; one tower embeds pictures, the other the query.
"""

from __future__ import annotations

import os
import sys
import threading
from collections.abc import Callable
from functools import cache

import numpy as np
from PIL import Image

from ..config import ImageEmbedding, TextEmbedding

_lock = threading.RLock()
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
os.environ.setdefault("HF_HUB_VERBOSITY", "error")


def _on_device[T](device: str, build: Callable[[str], T]) -> tuple[T, str]:
    """build(device); when the GPU has no room, say so once and build on the cpu instead."""
    import torch

    try:
        return build(device), device
    except (torch.cuda.OutOfMemoryError, RuntimeError) as e:
        if device == "cpu" or "out of memory" not in str(e).lower():
            raise
        print("[embed] GPU out of memory loading the model: using the cpu instead", file=sys.stderr)
        torch.cuda.empty_cache()
        return build("cpu"), "cpu"


def resolve_device(pref: str) -> str:
    import torch

    if pref != "auto":
        return pref
    return "cuda" if torch.cuda.is_available() else "cpu"


def _unit(x) -> np.ndarray:
    v = x.float().cpu().numpy() if hasattr(x, "cpu") else np.asarray(x, np.float32)
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-12)


class TextEmbedder:
    def __init__(self, cfg: TextEmbedding):
        import torch
        from sentence_transformers import SentenceTransformer
        from transformers.utils import logging as hf_logging

        hf_logging.disable_progress_bar()
        hf_logging.set_verbosity_error()

        self.cfg = cfg
        self.device = resolve_device(cfg.device)
        kwargs: dict = {"dtype": getattr(torch, cfg.dtype)}
        if cfg.quantize:
            from transformers import BitsAndBytesConfig

            kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=cfg.quantize == "4bit",
                load_in_8bit=cfg.quantize == "8bit",
                bnb_4bit_compute_dtype=torch.float16,
                bnb_4bit_quant_type="nf4",
            )
        if self.device == "cpu":
            kwargs["dtype"] = torch.float32
        self.model, self.device = _on_device(
            self.device,
            lambda d: SentenceTransformer(
                cfg.model,
                device=d,
                model_kwargs=kwargs,
                processor_kwargs={"padding_side": "left"},
            ),
        )
        self.model.max_seq_length = cfg.max_length

    def _encode(self, texts: list[str], prompt: str) -> np.ndarray:
        if not texts:
            return np.zeros((0, 0), np.float32)
        with _lock:
            v = self.model.encode(
                [prompt + t for t in texts],
                batch_size=self.cfg.batch_size,
                convert_to_numpy=True,
                normalize_embeddings=True,
            )
        return v.astype(np.float32)

    def documents(self, texts: list[str]) -> np.ndarray:
        return self._encode(texts, self.cfg.document_prompt)

    def queries(self, texts: list[str]) -> np.ndarray:
        return self._encode(texts, self.cfg.query_prompt)

    def likely_queries(self, texts: list[str]) -> np.ndarray:
        as_query = self.cfg.likely_query_prompt == "query"
        return self.queries(texts) if as_query else self.documents(texts)


class ImageEmbedder:
    """One tower of the picture model: "vision" embeds pictures, "text" embeds queries.

    Only the tower in use is kept, so each fits the 4 GB card on its own.
    """

    TEXT_TOKENS = 64  # SigLIP's text tower is trained on exactly this padded length

    def __init__(self, cfg: ImageEmbedding, tower: str):
        import torch
        from transformers import AutoModel, AutoProcessor
        from transformers.utils import logging as hf_logging

        hf_logging.disable_progress_bar()
        hf_logging.set_verbosity_error()

        self.cfg, self.tower = cfg, tower
        self.device = resolve_device(cfg.device if tower == "vision" else cfg.query_device)
        model = AutoModel.from_pretrained(cfg.model, dtype=torch.float32)
        drop = "text_model" if tower == "vision" else "vision_model"
        setattr(model, drop, torch.nn.Identity())
        model, self.device = _on_device(self.device, lambda d: model.to(d))
        self.model = model.eval()
        self.processor = AutoProcessor.from_pretrained(cfg.model)

    def images(self, imgs: list[Image.Image]) -> np.ndarray:
        import torch

        out = []
        with _lock, torch.inference_mode():
            for i in range(0, len(imgs), self.cfg.batch_size):
                px = self.processor(images=imgs[i : i + self.cfg.batch_size], return_tensors="pt")
                feats = self.model.get_image_features(
                    pixel_values=px["pixel_values"].to(self.device)
                )
                out.append(_unit(_features(feats)))
        return np.concatenate(out) if out else np.zeros((0, 0), np.float32)

    def texts(self, texts: list[str]) -> np.ndarray:
        import torch

        wrapped = [self.cfg.query_template.format(t) for t in texts]
        if self.cfg.lowercase:
            wrapped = [t.lower() for t in wrapped]
        with _lock, torch.inference_mode():
            tok = self.processor.tokenizer(
                wrapped,
                padding="max_length",
                max_length=self.TEXT_TOKENS,
                truncation=True,
                return_tensors="pt",
            ).to(self.device)
            return _unit(_features(self.model.get_text_features(**tok)))


def _features(out):
    """transformers 5 may return a model output instead of the projected tensor."""
    return out if hasattr(out, "cpu") else out.pooler_output


@cache
def text_embedder(cfg: TextEmbedding) -> TextEmbedder:
    with _lock:
        return TextEmbedder(cfg)


@cache
def image_embedder(cfg: ImageEmbedding, tower: str) -> ImageEmbedder:
    with _lock:
        return ImageEmbedder(cfg, tower)


def release() -> None:
    """Free the models' GPU memory (the embed stage runs text, then pictures)."""
    import gc

    text_embedder.cache_clear()
    image_embedder.cache_clear()
    gc.collect()
    import torch

    if torch.cuda.is_available():
        torch.cuda.empty_cache()
