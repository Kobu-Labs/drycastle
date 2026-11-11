"""Polite HTTP client: generic User-Agent, timeouts, retries with backoff, sequential calls."""

import time
from collections.abc import Callable

import requests

USER_AGENT = "suchohrad-pipeline"
RETRY_STATUS = {429, 500, 502, 503, 504}


class HttpError(Exception):
    """A request failed after all retries."""


class PoliteClient:
    def __init__(
        self,
        *,
        timeout: float = 120.0,
        retries: int = 4,
        backoff: float = 5.0,
        min_interval: float = 1.0,
        session: requests.Session | None = None,
        sleep: Callable[[float], None] = time.sleep,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.timeout = timeout
        self.retries = retries
        self.backoff = backoff
        self.min_interval = min_interval
        self.session = session or requests.Session()
        self.session.headers["User-Agent"] = USER_AGENT
        self._sleep = sleep
        self._clock = clock
        self._last: float | None = None

    def request(
        self,
        method: str,
        urls: str | list[str],
        *,
        params: dict | None = None,
        data: dict | None = None,
        headers: dict | None = None,
    ) -> tuple[requests.Response, str]:
        """Send the request, rotating through ``urls`` on retry. Returns (response, url used)."""
        candidates = [urls] if isinstance(urls, str) else list(urls)
        last_error = "no attempt made"
        for attempt in range(self.retries + 1):
            url = candidates[attempt % len(candidates)]
            if attempt:
                self._sleep(self.backoff * 2 ** (attempt - 1))
            self._throttle()
            try:
                resp = self.session.request(
                    method, url, params=params, data=data, headers=headers, timeout=self.timeout
                )
            except requests.RequestException as exc:
                last_error = type(exc).__name__
                continue
            if resp.status_code in RETRY_STATUS:
                last_error = f"HTTP {resp.status_code}"
                continue
            if resp.status_code >= 400:
                raise HttpError(f"HTTP {resp.status_code} from {url}")
            return resp, url
        raise HttpError(f"giving up after {self.retries + 1} attempts ({last_error})")

    def _throttle(self) -> None:
        now = self._clock()
        if self._last is not None:
            wait = self.min_interval - (now - self._last)
            if wait > 0:
                self._sleep(wait)
        self._last = self._clock()
