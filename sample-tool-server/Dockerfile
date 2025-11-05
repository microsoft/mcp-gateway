# Simple container for the dummy score API service
FROM python:3.12-slim

WORKDIR /app

# Create a non-root user
RUN useradd -m -u 1000 appuser && chown -R appuser:appuser /app

COPY requirements.txt ./
RUN pip install --no-cache-dir --root-user-action=ignore -r requirements.txt

# Copy the application code
COPY app.py ./

# Switch to non-root user
USER appuser

EXPOSE 8000

CMD ["python", "app.py"]
