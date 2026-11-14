from dataclasses import dataclass
from typing import Protocol


class BlobStore(Protocol):
    def put(self, pathname: str, body: bytes, content_type: str) -> str: ...
    def get(self, reference: str) -> bytes: ...
    def delete(self, reference: str) -> None: ...


@dataclass(frozen=True)
class StoredBlob:
    reference: str
    size: int
    content_type: str


class VercelBlobStore:
    """Private Vercel Blob adapter. Stored bodies are already client-encrypted."""

    def put(self, pathname: str, body: bytes, content_type: str) -> str:
        from vercel import blob
        import asyncio

        return asyncio.run(self._put(blob, pathname, body, content_type))

    async def _put(self, blob, pathname: str, body: bytes, content_type: str) -> str:
        result = await blob.put_blob(
            pathname,
            body,
            access="private",
            content_type=content_type,
            allow_overwrite=False,
        )
        return result.url

    def get(self, reference: str) -> bytes:
        from vercel import blob
        import asyncio

        return asyncio.run(self._get(blob, reference))

    async def _get(self, blob, reference: str) -> bytes:
        result = await blob.get_blob(reference, access="private")
        return result.body

    def delete(self, reference: str) -> None:
        from vercel import blob
        import asyncio

        asyncio.run(blob.delete_blob(reference))
