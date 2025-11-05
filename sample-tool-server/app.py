from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

app = FastAPI()


class WeatherRequest(BaseModel):
    location: str | None = None
    unit: str | None = None


@app.post("/score")
async def get_weather(payload: WeatherRequest):
    """Mocked weather endpoint matching the MCP tool contract."""
    location = payload.location
    unit = (payload.unit or "fahrenheit").lower()
    if not location:
        raise HTTPException(status_code=400, detail="Missing 'location' field.")
    if unit not in {"celsius", "fahrenheit"}:
        raise HTTPException(status_code=400, detail="Invalid 'unit' value.")

    # Static temperature keeps the mock predictable for tests.
    base_temp_f = 72.0
    temperature = base_temp_f if unit == "fahrenheit" else round((base_temp_f - 32) * 5 / 9, 1)
    return {
        "location": location,
        "unit": unit,
        "temperature": temperature,
        "conditions": "clear",
    }


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8000)
