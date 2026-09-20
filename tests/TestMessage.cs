using System;
using System.Drawing;
using System.Windows.Forms;
class TestMessage {
  [STAThread] static void Main(string[] args) {
    Application.EnableVisualStyles();
    var form=new Form { Text="业务沟通 · 项目 Alpha（自动验收约 20 秒）",Width=820,Height=300,StartPosition=FormStartPosition.CenterScreen,BackColor=Color.White };
    var label=new Label { Text="消息时间："+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\n明天下午三点开会，讨论接口联调问题",Dock=DockStyle.Fill,Padding=new Padding(35),Font=new Font("Microsoft YaHei UI",22),AutoSize=false };
    var start=DateTime.Now; int phase=0; var timer=new Timer { Interval=500 };
    timer.Tick+=(s,e)=>{
      double seconds=(DateTime.Now-start).TotalSeconds; int next=seconds>=12 ? 3 : seconds>=8 ? 2 : seconds>=4 ? 1 : 0;
      if(next==phase) return; phase=next;
      string body=phase==1 ? "会议改成下午四点" : phase==2 ? "会议取消了" : "今天"+(DateTime.Now.Hour>=12 ? "下午" : "上午")+DateTime.Now.AddMinutes(2).ToString("HH:mm")+"提交联调报告";
      label.Text="消息时间："+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\n"+body;
    };
    if(Array.IndexOf(args,"--scenario")>=0) timer.Start();
    form.FormClosing+=(s,e)=>System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture-exit.log"),DateTime.Now.ToString("o")+" "+e.CloseReason+" phase="+phase+Environment.NewLine);
    form.Controls.Add(label); Application.Run(form);
  }
}
