"""``python -m drycastle_pipeline.edits.mcp``: the MCP server over stdio."""

import sys

from drycastle_pipeline.edits.mcp.server import main

sys.exit(main())
