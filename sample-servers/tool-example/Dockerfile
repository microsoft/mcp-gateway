FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
	PYTHONUNBUFFERED=1 \
	PIP_DISABLE_PIP_VERSION_CHECK=1 \
	PIP_ROOT_USER_ACTION=ignore

WORKDIR /app

RUN useradd -m -u 1000 appuser

COPY requirements.txt .
RUN python -m pip install --upgrade pip \
	&& python -m pip install --no-cache-dir -r requirements.txt

COPY --chown=appuser:appuser app.py ./

USER appuser

EXPOSE 8000

CMD ["python", "app.py"]
