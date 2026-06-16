"""Pydantic request/response models."""
from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field

RiskBand = Literal["low", "medium", "high"]


class HealthResponse(BaseModel):
    status: str
    service: str
    dbConnected: bool


class NoShowRequest(BaseModel):
    bookingId: str = Field(..., description="UUID of the booking to score.")


class NoShowPrediction(BaseModel):
    bookingId: str
    noShowProbability: float = Field(..., ge=0.0, le=1.0)
    riskBand: RiskBand
    modelName: str
    modelVersion: str
    featuresUsed: dict


class NoShowTodayResponse(BaseModel):
    date: str
    count: int
    predictions: list[NoShowPrediction]


# --- RAG over patient medical history ---
class RagIndexRequest(BaseModel):
    patientId: str = Field(..., description="UUID of the patient to index.")


class RagIndexResponse(BaseModel):
    patientId: str
    recordsIndexed: int
    embeddingsTotal: int
    embeddingModel: str
    backend: str


class RagAskRequest(BaseModel):
    patientId: str = Field(..., description="UUID of the patient to query.")
    question: str = Field(..., min_length=1, description="Natural-language question.")


class RagCitation(BaseModel):
    historyId: str
    recordType: str | None = None
    title: str | None = None
    severity: str | None = None
    score: float


class RagAskResponse(BaseModel):
    patientId: str
    question: str
    answer: str
    mode: Literal["extractive", "llm"]
    citations: list[RagCitation]
    retrieved: int


class KnowledgeBaseInfo(BaseModel):
    kbKey: str
    name: str
    documentCount: int


class RagStatusResponse(BaseModel):
    embeddings: int
    patientsIndexed: int
    knowledgeBases: list[KnowledgeBaseInfo]


# --- OCR lab-report extraction ---
AnalyteFlag = Literal["low", "high", "normal"]


class LabReportExtractRequest(BaseModel):
    sourceUrl: str | None = Field(
        default=None,
        description="Path to the lab-report image. Defaults to the generated sample.",
    )
    relatedPatientId: str | None = Field(default=None, description="UUID of the patient.")
    relatedBookingId: str | None = Field(default=None, description="UUID of the booking.")


class Analyte(BaseModel):
    test: str
    value: float
    unit: str | None = None
    refLow: float
    refHigh: float
    flag: AnalyteFlag


class LabReportExtractResponse(BaseModel):
    extractionId: str
    sourceUrl: str
    ocrEngine: str
    overallConfidence: float
    requiresHumanReview: bool
    analytes: list[Analyte]
    abnormalCount: int
    rawTextPreview: str


class ExtractionListItem(BaseModel):
    extractionId: str
    sourceType: str
    status: str
    overallConfidence: float | None = None
    requiresHumanReview: bool
    abnormalCount: int
    createdAt: str


class ExtractionListResponse(BaseModel):
    count: int
    extractions: list[ExtractionListItem]


# --- Triage routing workflow (agentic) ---
UrgencyBand = Literal["low", "medium", "high", "emergency"]


class TriageRequest(BaseModel):
    complaint: str = Field(..., min_length=1, description="Free-text symptom complaint.")
    patientId: str | None = Field(default=None, description="UUID of the patient (PHI).")
    bookingId: str | None = Field(default=None, description="UUID of the booking (PHI).")
    patientAge: int | None = Field(default=None, ge=0, le=130, description="Patient age in years.")


class TriageUrgency(BaseModel):
    band: UrgencyBand
    redFlags: list[str]


class SuggestedDoctor(BaseModel):
    doctorId: str
    fullName: str
    specialization: str | None = None
    consultationFee: float | None = None
    nextAvailableSlot: str | None = None


class TriageStepSummary(BaseModel):
    stepNumber: int
    nodeName: str
    stepType: str
    summary: str


class TriageResponse(BaseModel):
    runId: str
    workflowKey: str
    symptoms: list[str]
    department: str
    urgency: TriageUrgency
    suggestedDoctors: list[SuggestedDoctor]
    steps: list[TriageStepSummary]


class TriageRunListItem(BaseModel):
    runId: str
    status: str
    department: str | None = None
    urgencyBand: str | None = None
    createdAt: str


class TriageRunListResponse(BaseModel):
    count: int
    runs: list[TriageRunListItem]


class TriageRunStep(BaseModel):
    stepNumber: int
    nodeName: str
    stepType: str
    success: bool
    durationMs: int | None = None
    toolInput: dict | None = None
    toolOutput: dict | None = None


class TriageRunDetailResponse(BaseModel):
    runId: str
    workflowKey: str
    status: str
    inputData: dict
    outputData: dict | None = None
    department: str | None = None
    urgencyBand: str | None = None
    startedAt: str
    completedAt: str | None = None
    durationMs: int | None = None
    steps: list[TriageRunStep]
