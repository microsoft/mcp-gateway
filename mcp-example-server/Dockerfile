FROM python:3.12-slim

# Create non-root user
RUN adduser --disabled-password --gecos '' --uid 1100 mcpuser

# Set working directory
WORKDIR /app

# Install dependencies
COPY requirements.txt .
RUN pip install --upgrade pip --root-user-action=ignore
RUN pip install --no-cache-dir -r requirements.txt --root-user-action=ignore

# Copy source
COPY . .

# Change ownership to non-root user
RUN chown -R mcpuser:mcpuser /app

# Switch to non-root user
USER 1100

# Expose HTTP port
EXPOSE 8000

# Start FastAPI via Uvicorn on HTTP
CMD ["fastmcp", "run", "src/main.py:app", "--transport", "streamable-http", "--port", "8000", "--host", "0.0.0.0"]