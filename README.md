- Cấu trúc của 1 thư mục chứa phiên bản của app  :
	- thư mục mẹ
    - version.json 
    - manifest.json
		- toàn bộ file và folder của phiên bản app cần quản lý

version.json

{
  "version": "x.x.x"
}

manifest tạo bằng mã code powershell taomanifest.ps1

- Cấu trúc file thông tin kết nối :

  - host=127.0.0.1
  - port=21
  - user=ftpuser
  - password=123456
  - remote=/         # optional remote start folder
  - encryption=none  # none | dpapi  (để app biết file này đã mã hóa không)

Chạy mahoa.ps1 , nhập từng thông tin cho file kết nối tới ftp server, sẽ tạo ra file ftp_credentials.dat

- Quy trình hoạt động :
	- Kết nối tới ftp server ,chương trình sẽ đọc thông tin file ftp_credentials.txt hoặc file ftp_credentials.dat 
	  từ vị trí C:\Users\TenCuaNguoiDung\AppData\Roaming\MyApp\ftp_credentials.dat
	- Chọn thư mục chứa ứng dụng ở local  (buộc có file manifest và version để so sánh thiếu file và cập nhật)
	- Chọn thư mục phiên bản cần cập nhật
	- nhấn chọn cập nhật
	- ô log sẽ hiển thị cập nhật xong
