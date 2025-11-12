FROM python:3.12-slim

# Create non-root user
RUN adduser --disabled-password --gecos '' --uid 1100 mcpuser

# Set working directory
WORKDIR /app

# Configure Python runtime defaults to avoid extraneous files and warnings
ENV PYTHONDONTWRITEBYTECODE=1 \
	PYTHONUNBUFFERED=1 \
	PIP_DISABLE_PIP_VERSION_CHECK=1 \
	PIP_ROOT_USER_ACTION=ignore

# Install dependencies
COPY requirements.txt .
RUN python -m pip install --upgrade pip \
	&& python -m pip install --no-cache-dir -r requirements.txt

# Copy source
COPY --chown=mcpuser:mcpuser . .

# Switch to non-root user
USER mcpuser

# Expose HTTP port
EXPOSE 8000

# Start FastAPI via Uvicorn on HTTP
CMD ["fastmcp", "run", "src/main.py:app", "--transport", "streamable-http", "--port", "8000", "--host", "0.0.0.0"]
