require("dotenv").config();

const fs = require("fs");
const path = require("path");
const axios = require("axios");
const sharp = require("sharp");
const API_URL = process.env.KIDSNOTE_API_URL;
const COOKIE = process.env.KIDSNOTE_COOKIE;
const baseDirectory = process.env.KIDSNOTE_BASE_DIR || "kids_note_images";
const startDate = new Date(process.env.KIDSNOTE_START_DATE || "2025-01-01");

function createFolder(folderPath) {
    if (!fs.existsSync(folderPath)) {
        fs.mkdirSync(folderPath, { recursive: true });
        console.log(`📂 폴더 생성: ${folderPath}`);
    }
}

async function fetchKidsNoteJson() {
    try {
        console.log("📡 KidsNote API 요청 중...");
        const res = await axios.get(API_URL, {
            headers: {
                "User-Agent": "Mozilla/5.0",
                "Accept": "application/json",
                "Referer": "https://www.kidsnote.com/",
                "Cookie": COOKIE,
            },
        });
        console.log("⏩ 응답 수신 완료");
        return res.data;
    } catch (error) {
        console.error("❌ API 요청 실패:", error.message);
        return null;
    }
}

async function downloadImageAsWebp(imageUrl, savePath) {
    if (fs.existsSync(savePath)) {
        console.log(`⏩ 이미 존재: ${savePath}`);
        return;
    }

    try {
        const res = await axios.get(imageUrl, { responseType: "arraybuffer" });
        const buffer = Buffer.from(res.data);
        await sharp(buffer).webp({ quality: 80 }).toFile(savePath);
        console.log(`✅ 저장 완료: ${savePath}`);
    } catch (err) {
        console.error(`❌ 실패: ${imageUrl}`, err.message);
    }
}

async function main() {
    const json = await fetchKidsNoteJson();
    if (!json || !json.results) {
        console.log("❌ JSON 구조가 올바르지 않음");
        return;
    }

    createFolder(baseDirectory);

    const today = new Date();

    for (const post of json.results) {
        const created = new Date(post.created);
        if (created < startDate || created > today) continue;

        if (post.author?.type !== "teacher" || !post.attached_images?.length) continue;

        const dateStr = post.created.split("T")[0];
        const folder = path.join(baseDirectory, dateStr);
        createFolder(folder);

        for (let i = 0; i < post.attached_images.length; i++) {
            const image = post.attached_images[i];
            const imageUrl = image.original || image.url;
            const filename = `${dateStr}-${i + 1}.webp`;
            const fullPath = path.join(folder, filename);

            await downloadImageAsWebp(imageUrl, fullPath);
        }
    }

    console.log("🎉 이미지 다운로드 완료!");
}

main();